import Foundation
import Observation

/// Состояние и операции вкладки «Корзина»: список удалённых файлов с cursor-пагинацией,
/// восстановление, удаление навсегда и полная очистка.
@MainActor
@Observable
final class TrashViewModel {
    struct UiState {
        var items: [TrashItem] = []
        var isLoading = true
        var isLoadingMore = false
        var canLoadMore = false
        /// Идёт блокирующая операция (очистка корзины целиком).
        var isProcessing = false
        var snackbar: String?

        fileprivate var cursorDeletedAt: Date?
        fileprivate var cursorEntryID: String = ""
    }

    var state = UiState()

    private let cloud: CloudRepository
    private var didLoad = false

    init(cloud: CloudRepository) { self.cloud = cloud }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    func reload() async {
        state.isLoading = true
        do {
            let page = try await cloud.listTrash(limit: 50)
            state.items = page.items
            state.cursorDeletedAt = page.nextCursorDeletedAt
            state.cursorEntryID = page.nextCursorEntryID
            state.canLoadMore = page.hasMore
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoading = false
    }

    func loadMoreIfNeeded(current item: TrashItem) async {
        guard state.canLoadMore, !state.isLoadingMore, item.id == state.items.last?.id else { return }
        state.isLoadingMore = true
        do {
            let page = try await cloud.listTrash(
                limit: 50,
                cursorDeletedAt: state.cursorDeletedAt,
                cursorEntryID: state.cursorEntryID
            )
            state.items.append(contentsOf: page.items)
            state.cursorDeletedAt = page.nextCursorDeletedAt
            state.cursorEntryID = page.nextCursorEntryID
            state.canLoadMore = page.hasMore
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoadingMore = false
    }

    func restore(_ item: TrashItem) async {
        do {
            try await cloud.restoreFromTrash(entryID: item.id)
            state.items.removeAll { $0.id == item.id }
            state.snackbar = String(localized: "trash_restored")
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    func deleteForever(_ item: TrashItem) async {
        do {
            try await cloud.deleteFromTrash(entryID: item.id)
            state.items.removeAll { $0.id == item.id }
            state.snackbar = String(localized: "trash_deleted")
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    func emptyAll() async {
        guard !state.isProcessing else { return }
        state.isProcessing = true
        do {
            try await cloud.emptyTrash()
            state.items = []
            state.canLoadMore = false
            state.cursorDeletedAt = nil
            state.cursorEntryID = ""
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isProcessing = false
    }

    func snackbarShown() { state.snackbar = nil }
}
