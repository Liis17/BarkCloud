import Foundation
import SwiftData

/// Запись локального кеша хешей: SHA256 оригинала ассета устройства по его
/// `localIdentifier`. Позволяет не пересчитывать тяжёлый потоковый хеш (особенно
/// для видео — читается каждый байт) при каждом скане/перезапуске.
@Model
final class AssetHashEntry {
    @Attribute(.unique) var localIdentifier: String
    var modificationDate: Date?
    var sha256: String
    var createdAt: Date

    init(localIdentifier: String, modificationDate: Date?, sha256: String, createdAt: Date) {
        self.localIdentifier = localIdentifier
        self.modificationDate = modificationDate
        self.sha256 = sha256
        self.createdAt = createdAt
    }
}

/// Постоянный (SwiftData) кеш «localIdentifier → SHA256 оригинала». Один на
/// приложение (`shared`) — отдельная БД `BarkCloudAssetHashes.sqlite` в Application
/// Support; при сбое открытия откатывается на in-memory, чтобы не уронить
/// функциональность. Инвалидация — по `modificationDate` ассета: если фото/видео
/// отредактировали, кеш считается устаревшим и хеш пересчитывается.
actor AssetHashStore {
    static let shared = AssetHashStore()

    private let container: ModelContainer

    private init() {
        let fm = FileManager.default
        let appSupport = URL.applicationSupportDirectory
        try? fm.createDirectory(at: appSupport, withIntermediateDirectories: true)
        let storeURL = appSupport.appendingPathComponent("BarkCloudAssetHashes.sqlite")
        if let c = try? ModelContainer(
            for: AssetHashEntry.self,
            configurations: ModelConfiguration(url: storeURL)
        ) {
            container = c
        } else {
            container = try! ModelContainer(
                for: AssetHashEntry.self,
                configurations: ModelConfiguration(isStoredInMemoryOnly: true)
            )
        }
    }

    /// Закешированный хеш ассета, если он валиден (`modificationDate` совпадает).
    /// Иначе `nil` — нужно пересчитать (запись с устаревшей датой удаляется).
    func hash(forLocalId id: String, modificationDate: Date?) -> String? {
        let context = ModelContext(container)
        var descriptor = FetchDescriptor<AssetHashEntry>(predicate: #Predicate { $0.localIdentifier == id })
        descriptor.fetchLimit = 1
        guard let entry = try? context.fetch(descriptor).first else { return nil }
        if entry.modificationDate != modificationDate {
            context.delete(entry)
            try? context.save()
            return nil
        }
        return entry.sha256
    }

    /// Сохранить (или обновить) хеш ассета.
    func store(localId id: String, modificationDate: Date?, sha256: String) {
        let context = ModelContext(container)
        var descriptor = FetchDescriptor<AssetHashEntry>(predicate: #Predicate { $0.localIdentifier == id })
        descriptor.fetchLimit = 1
        if let entry = try? context.fetch(descriptor).first {
            entry.sha256 = sha256
            entry.modificationDate = modificationDate
        } else {
            context.insert(AssetHashEntry(
                localIdentifier: id,
                modificationDate: modificationDate,
                sha256: sha256,
                createdAt: .now
            ))
        }
        try? context.save()
    }

    /// Удалить записи для перечисленных ассетов — вызывается после того, как фото/видео
    /// удалены с устройства (освобождение места), чтобы кеш не держал мёртвые `localIdentifier`.
    func remove(localIds ids: [String]) {
        guard !ids.isEmpty else { return }
        let context = ModelContext(container)
        let descriptor = FetchDescriptor<AssetHashEntry>(
            predicate: #Predicate { ids.contains($0.localIdentifier) }
        )
        guard let entries = try? context.fetch(descriptor), !entries.isEmpty else { return }
        for entry in entries { context.delete(entry) }
        try? context.save()
    }

    /// Полная очистка — вызывается при выходе из аккаунта (`resetLocalState`).
    func clearAll() {
        let context = ModelContext(container)
        try? context.delete(model: AssetHashEntry.self)
        try? context.save()
    }
}
