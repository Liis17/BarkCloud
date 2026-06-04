import Foundation
import SwiftData

/// Связь «облачный файл ↔ ассет устройства»: `file_id` блоба в облаке →
/// `localIdentifier` фото/видео в медиатеке. Нужна для синхронного удаления —
/// удалив файл в облаке, находим и убираем его копию на устройстве. Заполняется
/// только когда клиент достоверно знает обе стороны связи: при загрузке ассета в
/// облако (известны и `file_id`, и `localIdentifier`) и при подтверждении наличия
/// по SHA256 (`CloudPresenceTracker`).
@Model
final class CloudDeviceLink {
    @Attribute(.unique) var fileID: String
    var localIdentifier: String
    var createdAt: Date

    init(fileID: String, localIdentifier: String, createdAt: Date) {
        self.fileID = fileID
        self.localIdentifier = localIdentifier
        self.createdAt = createdAt
    }
}

/// Постоянный (SwiftData) индекс связей `file_id ↔ localIdentifier`. Один на
/// приложение (`shared`) — отдельная БД `BarkCloudCloudDeviceLinks.sqlite` в
/// Application Support; при сбое открытия откатывается на in-memory, чтобы не
/// уронить функциональность. Образец — `AssetHashStore`.
actor CloudDeviceLinkStore {
    static let shared = CloudDeviceLinkStore()

    private let container: ModelContainer

    private init() {
        let fm = FileManager.default
        let appSupport = URL.applicationSupportDirectory
        try? fm.createDirectory(at: appSupport, withIntermediateDirectories: true)
        let storeURL = appSupport.appendingPathComponent("BarkCloudCloudDeviceLinks.sqlite")
        if let c = try? ModelContainer(
            for: CloudDeviceLink.self,
            configurations: ModelConfiguration(url: storeURL)
        ) {
            container = c
        } else {
            container = try! ModelContainer(
                for: CloudDeviceLink.self,
                configurations: ModelConfiguration(isStoredInMemoryOnly: true)
            )
        }
    }

    /// Запомнить связь. `file_id` уникален — при повторе обновляем `localIdentifier`.
    func link(fileID: String, localIdentifier: String) {
        guard !fileID.isEmpty, !localIdentifier.isEmpty else { return }
        let context = ModelContext(container)
        var descriptor = FetchDescriptor<CloudDeviceLink>(predicate: #Predicate { $0.fileID == fileID })
        descriptor.fetchLimit = 1
        if let entry = try? context.fetch(descriptor).first {
            entry.localIdentifier = localIdentifier
        } else {
            context.insert(CloudDeviceLink(fileID: fileID, localIdentifier: localIdentifier, createdAt: .now))
        }
        try? context.save()
    }

    /// `localIdentifier` ассета устройства для облачного `file_id`, если связь известна.
    func localIdentifier(forFileID fileID: String) -> String? {
        let context = ModelContext(container)
        var descriptor = FetchDescriptor<CloudDeviceLink>(predicate: #Predicate { $0.fileID == fileID })
        descriptor.fetchLimit = 1
        return (try? context.fetch(descriptor).first)?.localIdentifier
    }

    /// Удалить связи по `file_id` (после удаления файла из облака/устройства).
    func remove(fileIDs ids: [String]) {
        guard !ids.isEmpty else { return }
        let context = ModelContext(container)
        let descriptor = FetchDescriptor<CloudDeviceLink>(predicate: #Predicate { ids.contains($0.fileID) })
        guard let entries = try? context.fetch(descriptor), !entries.isEmpty else { return }
        for entry in entries { context.delete(entry) }
        try? context.save()
    }

    /// Удалить связи по `localIdentifier` (после удаления ассета с устройства).
    func remove(localIds ids: [String]) {
        guard !ids.isEmpty else { return }
        let context = ModelContext(container)
        let descriptor = FetchDescriptor<CloudDeviceLink>(predicate: #Predicate { ids.contains($0.localIdentifier) })
        guard let entries = try? context.fetch(descriptor), !entries.isEmpty else { return }
        for entry in entries { context.delete(entry) }
        try? context.save()
    }

    /// Полная очистка — при выходе из аккаунта (`resetLocalState`).
    func clearAll() {
        let context = ModelContext(container)
        try? context.delete(model: CloudDeviceLink.self)
        try? context.save()
    }
}
