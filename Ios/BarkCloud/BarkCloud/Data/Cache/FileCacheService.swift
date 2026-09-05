import Foundation
import SwiftData
import UniformTypeIdentifiers

enum FileCacheError: Error {
    case downloadFailed
}

/// Единая точка доступа к постоянному дисковому кешу файлов. Хранит метаданные в
/// SwiftData, а байты — в `Library/Caches/BarkCloudFiles/`. При наличии файла в
/// кеше отдаёт его без сети; при отсутствии — скачивает через `http`, сохраняет и
/// обновляет `lastAccessAt`. Обслуживание (eviction по возрасту и по размеру)
/// делают `evictStale` / `enforceSizeLimit` / `runStartupSweepIfNeeded`.
actor FileCacheService {
    private let modelContainer: ModelContainer
    private let settings: FileCacheSettings
    private let http: URLSession
    private let rootURL: URL

    init(modelContainer: ModelContainer, settings: FileCacheSettings, http: URLSession) {
        self.modelContainer = modelContainer
        self.settings = settings
        self.http = http
        self.rootURL = URL.cachesDirectory.appendingPathComponent("BarkCloudFiles", isDirectory: true)
        try? FileManager.default.createDirectory(at: rootURL, withIntermediateDirectories: true)
    }

    // MARK: - Публичное API

    /// Вернуть URL файла на диске, скачав при необходимости через `urlResolver`
    /// (для оригиналов — closure с `GetTempDownloadUrl`). Используется QuickLook'ом.
    func loadFile(
        fileId: String,
        variant: CacheVariant,
        urlResolver: () async throws -> URL
    ) async throws -> URL {
        let key = CachedFileEntry.key(fileId: fileId, variant: variant)
        let context = ModelContext(modelContainer)
        if let entry = entry(forKey: key, in: context) {
            let fileURL = rootURL.appendingPathComponent(entry.relativePath)
            if FileManager.default.fileExists(atPath: fileURL.path) {
                entry.lastAccessAt = .now
                try? context.save()
                return fileURL
            }
            context.delete(entry)
            try? context.save()
        }

        let remote = try await urlResolver()
        let (tempURL, response) = try await http.download(from: remote)
        defer { try? FileManager.default.removeItem(at: tempURL) }
        guard let httpResp = response as? HTTPURLResponse, (200..<300).contains(httpResp.statusCode) else {
            throw FileCacheError.downloadFailed
        }
        let ext = fileExtension(from: response, fallbackURL: remote)
        let dest = try moveIntoCache(tempURL, fileId: fileId, variant: variant, ext: ext)
        recordEntry(fileId: fileId, variant: variant, sourceURL: remote, destination: dest)
        await enforceSizeLimit()
        return dest
    }

    /// Быстрый путь для случаев, когда URL уже на руках (превью, аватары): вернуть
    /// байты из кеша или скачать и закешировать.
    func loadData(
        fileId: String,
        variant: CacheVariant,
        sourceURL: URL
    ) async throws -> Data {
        let key = CachedFileEntry.key(fileId: fileId, variant: variant)
        let context = ModelContext(modelContainer)
        if let entry = entry(forKey: key, in: context) {
            let fileURL = rootURL.appendingPathComponent(entry.relativePath)
            if let data = try? Data(contentsOf: fileURL) {
                entry.lastAccessAt = .now
                try? context.save()
                return data
            }
            context.delete(entry)
            try? context.save()
        }

        let (data, response) = try await http.data(from: sourceURL)
        if let httpResp = response as? HTTPURLResponse, !(200..<300).contains(httpResp.statusCode) {
            throw FileCacheError.downloadFailed
        }
        let ext = fileExtension(from: response, fallbackURL: sourceURL)
        let dest = try writeIntoCache(data, fileId: fileId, variant: variant, ext: ext)
        recordEntry(fileId: fileId, variant: variant, sourceURL: sourceURL, destination: dest)
        await enforceSizeLimit()
        return data
    }

    // MARK: - Обслуживание

    func totalSize() -> Int64 {
        let context = ModelContext(modelContainer)
        let entries = (try? context.fetch(FetchDescriptor<CachedFileEntry>())) ?? []
        return entries.reduce(0) { $0 + $1.sizeBytes }
    }

    func entryCount() -> Int {
        let context = ModelContext(modelContainer)
        return (try? context.fetchCount(FetchDescriptor<CachedFileEntry>())) ?? 0
    }

    /// Удалить записи, к которым не обращались дольше `olderThan` (дефолт — 7 дней),
    /// вместе с файлами на диске.
    func evictStale(olderThan: TimeInterval = 7 * 24 * 3600) {
        let threshold = Date.now.addingTimeInterval(-olderThan)
        let context = ModelContext(modelContainer)
        let descriptor = FetchDescriptor<CachedFileEntry>(
            predicate: #Predicate { $0.lastAccessAt < threshold }
        )
        guard let stale = try? context.fetch(descriptor) else { return }
        for entry in stale { remove(entry, in: context) }
        try? context.save()
    }

    /// Вытеснять записи (LRU по `lastAccessAt`), пока суммарный размер превышает лимит.
    func enforceSizeLimit() {
        let limit = settings.maxCacheBytes
        let context = ModelContext(modelContainer)
        var descriptor = FetchDescriptor<CachedFileEntry>(
            sortBy: [SortDescriptor(\.lastAccessAt, order: .forward)]
        )
        descriptor.propertiesToFetch = [\.sizeBytes, \.relativePath, \.key]
        guard let entries = try? context.fetch(descriptor) else { return }
        var total = entries.reduce(Int64(0)) { $0 + $1.sizeBytes }
        guard total > limit else { return }
        for entry in entries where total > limit {
            total -= entry.sizeBytes
            remove(entry, in: context)
        }
        try? context.save()
    }

    /// Полная очистка кеша: все записи БД + каталог на диске.
    func clearAll() {
        let context = ModelContext(modelContainer)
        try? context.delete(model: CachedFileEntry.self)
        try? context.save()
        try? FileManager.default.removeItem(at: rootURL)
        try? FileManager.default.createDirectory(at: rootURL, withIntermediateDirectories: true)
    }

    /// Не чаще раза в сутки при старте: вычистить устаревшее (по настраиваемому
    /// порогу `staleMaxAge`; `nil` — пропустить возрастную очистку) и уложиться в лимит.
    func runStartupSweepIfNeeded() {
        let sweepInterval: TimeInterval = 24 * 3600
        if let last = settings.lastSweepAt, Date.now.timeIntervalSince(last) < sweepInterval {
            return
        }
        if let maxAge = settings.staleMaxAge {
            evictStale(olderThan: maxAge)
        }
        enforceSizeLimit()
        settings.lastSweepAt = .now
    }

    // MARK: - Приватное

    private func entry(forKey key: String, in context: ModelContext) -> CachedFileEntry? {
        var descriptor = FetchDescriptor<CachedFileEntry>(predicate: #Predicate { $0.key == key })
        descriptor.fetchLimit = 1
        return try? context.fetch(descriptor).first
    }

    private func remove(_ entry: CachedFileEntry, in context: ModelContext) {
        let fileURL = rootURL.appendingPathComponent(entry.relativePath)
        try? FileManager.default.removeItem(at: fileURL)
        context.delete(entry)
    }

    private func directory(for fileId: String) throws -> URL {
        let dir = rootURL.appendingPathComponent(fileId, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    private func fileName(for variant: CacheVariant, ext: String) -> String {
        ext.isEmpty ? variant.storageKey : "\(variant.storageKey).\(ext)"
    }

    private func writeIntoCache(_ data: Data, fileId: String, variant: CacheVariant, ext: String) throws -> URL {
        let dest = try directory(for: fileId).appendingPathComponent(fileName(for: variant, ext: ext))
        try data.write(to: dest, options: .atomic)
        return dest
    }

    private func moveIntoCache(_ tempURL: URL, fileId: String, variant: CacheVariant, ext: String) throws -> URL {
        let dest = try directory(for: fileId).appendingPathComponent(fileName(for: variant, ext: ext))
        try? FileManager.default.removeItem(at: dest)
        try FileManager.default.moveItem(at: tempURL, to: dest)
        return dest
    }

    private func recordEntry(fileId: String, variant: CacheVariant, sourceURL: URL, destination: URL) {
        let size = (try? destination.resourceValues(forKeys: [.fileSizeKey]).fileSize).flatMap { $0 } ?? 0
        let relativePath = destination.path.replacingOccurrences(of: rootURL.path + "/", with: "")
        let context = ModelContext(modelContainer)
        let key = CachedFileEntry.key(fileId: fileId, variant: variant)
        if let existing = entry(forKey: key, in: context) {
            remove(existing, in: context)
        }
        let entry = CachedFileEntry(
            key: key,
            fileId: fileId,
            variant: variant.storageKey,
            sourceURL: sourceURL.absoluteString,
            relativePath: relativePath,
            sizeBytes: Int64(size),
            lastAccessAt: .now,
            createdAt: .now
        )
        context.insert(entry)
        try? context.save()
    }

    private func fileExtension(from response: URLResponse, fallbackURL: URL) -> String {
        if let name = response.suggestedFilename {
            let ext = (name as NSString).pathExtension
            if !ext.isEmpty { return ext }
        }
        if let mime = response.mimeType,
           let type = UTType(mimeType: mime),
           let ext = type.preferredFilenameExtension {
            return ext
        }
        return fallbackURL.pathExtension
    }
}
