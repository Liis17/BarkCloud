import Foundation
import Observation

/// Состояние таба «Мои публичные» экрана `SharedHubScreen`. Пагинируется
/// курсором: первая страница в `loadIfNeeded()`, далее `loadMoreIfNeeded`
/// дёргается из `.onAppear` на последней карточке списка.
struct MySharesUiState {
    var items: [ShareLink] = []
    /// До первой удачной загрузки рисуем плейсхолдер (скелетоны).
    var isPlaceholder: Bool = true
    var isLoadingMore: Bool = false
    var canLoadMore: Bool = false
    var snackbar: String?

    fileprivate var cursorCreatedAt: Date?
    fileprivate var cursorShareID: String = ""
}

/// View-model раздела «Мои публичные ссылки». Источник истины — `CloudRepository`.
///
/// Revoke реализован оптимистично: убираем из массива сразу после нажатия, при
/// ошибке возвращаем и показываем snackbar. Это безопасно потому что
/// `revokeShare` идемпотентен на бэкенде (повторный отзыв уже отозванной
/// ссылки проходит без ошибки).
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
        do {
            let page = try await cloud.listMyShares(limit: 60)
            state.items = page.items
            state.cursorCreatedAt = page.nextCursorCreatedAt
            state.cursorShareID = page.nextCursorShareID
            state.canLoadMore = page.hasMore
        } catch {
            state.items = []
            state.snackbar = String(localized: "shared_load_failed")
        }
        state.isPlaceholder = false
    }

    func loadMoreIfNeeded(current item: ShareLink) async {
        guard state.canLoadMore, !state.isLoadingMore, !state.isPlaceholder,
              item.id == state.items.last?.id else { return }
        state.isLoadingMore = true
        do {
            let page = try await cloud.listMyShares(
                limit: 60,
                cursorCreatedAt: state.cursorCreatedAt,
                cursorShareID: state.cursorShareID
            )
            state.items.append(contentsOf: page.items)
            state.cursorCreatedAt = page.nextCursorCreatedAt
            state.cursorShareID = page.nextCursorShareID
            state.canLoadMore = page.hasMore
        } catch {
            state.snackbar = String(localized: "shared_load_failed")
        }
        state.isLoadingMore = false
    }

    /// Оптимистично удалить из списка и отозвать на бэкенде. При ошибке
    /// возвращаем элемент на то же место (по индексу).
    func revoke(_ link: ShareLink) async {
        guard let idx = state.items.firstIndex(where: { $0.id == link.id }) else { return }
        state.items.remove(at: idx)
        do {
            try await cloud.revokeShare(id: link.id)
            state.snackbar = String(localized: "shared_link_revoked")
        } catch {
            state.items.insert(link, at: min(idx, state.items.count))
            state.snackbar = String(localized: "shared_revoke_failed")
        }
    }

    func snackbarShown() { state.snackbar = nil }
}
