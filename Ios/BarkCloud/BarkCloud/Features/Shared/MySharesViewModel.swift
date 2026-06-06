import Foundation
import Observation
import BarkCloudKit

/// Унифицированная публичная ссылка для таба «Мои публичные» — файл, папка или
/// альбом в одном списке. `recordID` — id записи (для отзыва); `id` с префиксом
/// разводит одинаковые id разных типов в `ForEach`.
struct PublicShareItem: Identifiable, Hashable {
    enum Kind: Hashable { case file, folder, album }

    let kind: Kind
    let recordID: String
    let name: String
    let url: URL?
    let clickCount: Int
    let createdAt: Date

    var id: String {
        switch kind {
        case .file:   return "f:" + recordID
        case .folder: return "d:" + recordID
        case .album:  return "a:" + recordID
        }
    }

    init(_ l: ShareLink) {
        kind = .file; recordID = l.id; name = l.name; url = l.url
        clickCount = l.clickCount; createdAt = l.createdAt
    }

    init(_ l: FolderShareLink) {
        kind = .folder; recordID = l.id; name = l.name; url = l.url
        clickCount = l.clickCount; createdAt = l.createdAt
    }

    init(_ l: AlbumShareLink) {
        kind = .album; recordID = l.id; name = l.name; url = l.url
        clickCount = l.clickCount; createdAt = l.createdAt
    }
}

struct MySharesUiState {
    var items: [PublicShareItem] = []
    /// До первой удачной загрузки рисуем плейсхолдер (спиннер).
    var isPlaceholder: Bool = true
    var snackbar: String?
    /// URL для диалога «Скопировать / Поделиться…» (при тапе на «Поделиться» у карточки).
    var pendingShareURL: ShareableURL?
}

/// View-model раздела «Мои публичные ссылки». Грузит публичные ссылки **трёх**
/// типов (файлы, папки, альбомы) и сводит в один список, отсортированный от
/// свежих к старым — как вкладка «Мои публичные» на вебе. Пагинации нет: берём
/// до 200 каждого типа (потолок бэкенда), чего хватает для управления.
///
/// Revoke оптимистичен: убираем из массива сразу, при ошибке возвращаем. На
/// бэкенде отзыв идемпотентен (повторный проходит без ошибки).
@MainActor
@Observable
final class MySharesViewModel {
    var state = MySharesUiState()

    private let cloud: CloudRepository
    private var didLoad = false

    init(cloud: CloudRepository) {
        self.cloud = cloud
    }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    func reload() async {
        var items: [PublicShareItem] = []
        var failed = false
        do {
            items += try await cloud.listMyShares(limit: 200).items.map(PublicShareItem.init)
        } catch {
            failed = true
        }
        // Папки и альбомы — best-effort: их отсутствие/ошибка не должны рушить
        // весь список файловых ссылок.
        if let folders = try? await cloud.listMyFolderShares(limit: 200) {
            items += folders.items.map(PublicShareItem.init)
        }
        if let albums = try? await cloud.listMyAlbumShares(limit: 200) {
            items += albums.items.map(PublicShareItem.init)
        }
        state.items = items.sorted { $0.createdAt > $1.createdAt }
        if failed && state.items.isEmpty {
            state.snackbar = String(localized: "shared_load_failed")
        }
        state.isPlaceholder = false
    }

    /// Оптимистично удалить из списка и отозвать на бэкенде. Маршрутизация
    /// отзыва — по типу ссылки. При ошибке возвращаем элемент на место.
    func revoke(_ item: PublicShareItem) async {
        guard let idx = state.items.firstIndex(where: { $0.id == item.id }) else { return }
        state.items.remove(at: idx)
        do {
            switch item.kind {
            case .file:   try await cloud.revokeShare(id: item.recordID)
            case .folder: try await cloud.revokeFolderShare(id: item.recordID)
            case .album:  try await cloud.revokeAlbumShare(id: item.recordID)
            }
            state.snackbar = String(localized: "shared_link_revoked")
        } catch {
            state.items.insert(item, at: min(idx, state.items.count))
            state.snackbar = String(localized: "shared_revoke_failed")
        }
    }

    func snackbarShown() { state.snackbar = nil }
}
