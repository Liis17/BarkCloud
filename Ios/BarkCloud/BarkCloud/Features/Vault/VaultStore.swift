import Foundation
import Observation

/// Снимок медиа, помещённого в локальный «сейф». Хранит то, что нужно сетке сейфа
/// без сетевого запроса: `preview_url` бэкенда публичен и постоянен, поэтому его
/// можно держать локально. Оригинал открывается по `id` через `GetTempDownloadUrl`.
struct VaultItem: Codable, Identifiable, Hashable {
    let id: String              // file_id блоба
    let thumbnailURL: URL?
    let previewWidth: Int
    let isVideo: Bool
    let fileName: String
}

/// Локальный «сейф»: список `file_id` облачных фото/видео, скрытых из обычной
/// галереи и доступных только после биометрической разблокировки.
///
/// Хранится **только на устройстве** (`UserDefaults`) — сервер о защите не знает,
/// для него это обычные файлы. При полном сбросе (`AppEnvironment.resetLocalState()`:
/// выход, удаление аккаунта, wipe по неверному PIN) список чистится вместе с остальными
/// данными, чтобы скрытые файлы прежнего аккаунта не оставались на устройстве.
@MainActor
@Observable
final class VaultStore {
    private let defaults: UserDefaults
    private let key = "BarkCloud.vault.items"

    private(set) var items: [VaultItem]

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        if let data = defaults.data(forKey: key),
           let decoded = try? JSONDecoder().decode([VaultItem].self, from: data) {
            self.items = decoded
        } else {
            self.items = []
        }
        // Засеять счётчик виджета на старте (для уже существующего сейфа).
        VaultWidgetBridge.update(count: items.count)
    }

    /// Идентификаторы защищённых файлов — для фильтрации обычных списков.
    var protectedIDs: Set<String> { Set(items.map(\.id)) }

    func contains(_ id: String) -> Bool { items.contains { $0.id == id } }

    var isEmpty: Bool { items.isEmpty }

    /// Добавить элементы в сейф (дубликаты по `id` игнорируются).
    func add(_ newItems: [VaultItem]) {
        let existing = protectedIDs
        let toAdd = newItems.filter { !existing.contains($0.id) }
        guard !toAdd.isEmpty else { return }
        items.append(contentsOf: toAdd)
        persist()
    }

    /// Убрать элементы из сейфа (вернуть в обычную галерею).
    func remove(ids: Set<String>) {
        guard !ids.isEmpty else { return }
        items.removeAll { ids.contains($0.id) }
        persist()
    }

    /// Полная очистка локального сейфа. Зовётся из `AppEnvironment.resetLocalState()`
    /// (выход / удаление аккаунта / wipe по 3 неверным PIN).
    func removeAll() {
        guard !items.isEmpty else { return }
        items.removeAll()
        persist()
    }

    private func persist() {
        if let data = try? JSONEncoder().encode(items) {
            defaults.set(data, forKey: key)
        }
        // Отдать счётчик виджету сейфа (он сам решит, показывать число или только замок).
        VaultWidgetBridge.update(count: items.count)
    }
}
