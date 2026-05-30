import Foundation
import Photos
import CryptoKit

enum DeviceAssetError: Error { case noResource }

/// Общие утилиты чтения оригиналов ассетов устройства через PhotoKit.
/// Используются и вкладкой «Галерея», и кастомным пикером загрузки: оба должны
/// читать байты и считать SHA256 одинаково (тот же приоритет ресурсов), чтобы
/// хеш совпадал с тем, что бэкенд вычисляет при загрузке (дедупликация).
enum DeviceAssetResource {
    /// Приоритет ресурсов: сначала исходник, потом полноразмерные варианты.
    private static let preferredTypes: [PHAssetResourceType] = [.photo, .video, .fullSizePhoto, .fullSizeVideo]

    private static func bestResource(for asset: PHAsset) -> PHAssetResource? {
        let resources = PHAssetResource.assetResources(for: asset)
        return preferredTypes.compactMap { type in resources.first { $0.type == type } }.first
            ?? resources.first
    }

    /// Потокобезопасный аккумулятор чанков (PHAssetResourceManager отдаёт данные
    /// частями, возможно с фонового потока).
    private final class DataBuffer: @unchecked Sendable {
        private var data = Data()
        private let lock = NSLock()
        func append(_ chunk: Data) { lock.lock(); data.append(chunk); lock.unlock() }
        var value: Data { lock.lock(); defer { lock.unlock() }; return data }
    }

    /// Размер оригинала ассета в байтах (для оценки «сколько освободится»). Берётся
    /// из приватного `fileSize` того же ресурса, что идёт в загрузку — широко
    /// используемый KVC-приём PhotoKit. `0`, если размер недоступен.
    static func originalByteSize(for asset: PHAsset) -> Int64 {
        guard let resource = bestResource(for: asset) else { return 0 }
        return (resource.value(forKey: "fileSize") as? Int64) ?? 0
    }

    /// Записать оригинал ассета в файл (поток с диска медиатеки → файл), без
    /// загрузки в RAM. Используется для постановки в `BackgroundUploadCoordinator`:
    /// background URLSession принимает только `fromFile:`, и держать всё видео в
    /// памяти — гарантированный crash при больших файлах. Возвращает имя файла.
    @discardableResult
    static func writeOriginal(asset: PHAsset, to destination: URL) async throws -> String {
        guard let resource = bestResource(for: asset) else { throw DeviceAssetError.noResource }
        let fm = FileManager.default
        try fm.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? fm.removeItem(at: destination)
        guard fm.createFile(atPath: destination.path, contents: nil) else {
            throw DeviceAssetError.noResource
        }
        let handle = try FileHandle(forWritingTo: destination)
        let options = PHAssetResourceRequestOptions()
        options.isNetworkAccessAllowed = true

        // Делегатное чтение чанков с фонового потока — пишем в FileHandle под
        // защитой NSLock (writeData может оказаться вызванным с разных потоков).
        let writer = ChunkFileWriter(handle: handle)
        do {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                PHAssetResourceManager.default().requestData(
                    for: resource,
                    options: options,
                    dataReceivedHandler: { chunk in writer.write(chunk) },
                    completionHandler: { error in
                        if let error { cont.resume(throwing: error) } else { cont.resume() }
                    }
                )
            }
        } catch {
            writer.close()
            try? fm.removeItem(at: destination)
            throw error
        }
        writer.close()
        return resource.originalFilename
    }

    /// Потокобезопасный writer чанков в FileHandle.
    private final class ChunkFileWriter: @unchecked Sendable {
        private let handle: FileHandle
        private let lock = NSLock()
        init(handle: FileHandle) { self.handle = handle }
        func write(_ chunk: Data) {
            lock.lock(); defer { lock.unlock() }
            try? handle.write(contentsOf: chunk)
        }
        func close() {
            lock.lock(); defer { lock.unlock() }
            try? handle.close()
        }
    }

    /// Оригинальные байты ассета и его имя файла (для загрузки в облако).
    static func originalData(for asset: PHAsset) async throws -> (Data, String) {
        guard let resource = bestResource(for: asset) else { throw DeviceAssetError.noResource }

        let fileName = resource.originalFilename
        let buffer = DataBuffer()
        let options = PHAssetResourceRequestOptions()
        options.isNetworkAccessAllowed = true

        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            PHAssetResourceManager.default().requestData(
                for: resource,
                options: options,
                dataReceivedHandler: { chunk in buffer.append(chunk) },
                completionHandler: { error in
                    if let error { cont.resume(throwing: error) } else { cont.resume() }
                }
            )
        }
        return (buffer.value, fileName)
    }

    /// Потокобезопасный SHA256-хешер (обновляется чанками с фонового потока).
    private final class HasherBox: @unchecked Sendable {
        private var hasher = SHA256()
        private let lock = NSLock()
        func update(_ chunk: Data) { lock.lock(); hasher.update(data: chunk); lock.unlock() }
        func hexDigest() -> String {
            lock.lock(); defer { lock.unlock() }
            return hasher.finalize().map { String(format: "%02x", $0) }.joined()
        }
    }

    /// SHA256 оригинала с персистентным кешем (`AssetHashStore`): сначала пробуем
    /// взять готовый хеш по `localIdentifier`, иначе считаем потоково и сохраняем.
    /// Тяжёлое чтение каждого байта (особенно для видео) выполняется один раз, а
    /// при последующих сканах/перезапусках берётся из локальной БД.
    static func cachedSHA256(for asset: PHAsset) async -> String? {
        let id = asset.localIdentifier
        let mod = asset.modificationDate
        if let cached = await AssetHashStore.shared.hash(forLocalId: id, modificationDate: mod) {
            return cached
        }
        guard let hash = await streamingSHA256(for: asset) else { return nil }
        await AssetHashStore.shared.store(localId: id, modificationDate: mod, sha256: hash)
        return hash
    }

    /// SHA256 оригинала ассета в hex (lowercase) — считается потоково, без
    /// удержания всего файла в памяти. Должен совпадать с хешем, который бэкенд
    /// вычисляет при загрузке (тот же приоритет ресурсов, что и в `originalData`).
    static func streamingSHA256(for asset: PHAsset) async -> String? {
        guard let resource = bestResource(for: asset) else { return nil }

        let options = PHAssetResourceRequestOptions()
        options.isNetworkAccessAllowed = true
        let hasher = HasherBox()
        do {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                PHAssetResourceManager.default().requestData(
                    for: resource,
                    options: options,
                    dataReceivedHandler: { chunk in hasher.update(chunk) },
                    completionHandler: { error in
                        if let error { cont.resume(throwing: error) } else { cont.resume() }
                    }
                )
            }
        } catch {
            return nil
        }
        return hasher.hexDigest()
    }
}
