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

    /// Отложенное «удалить навсегда» — внизу появляется snackbar с отсчётом и
    /// кнопкой «Отменить». До истечения таймера запрос на сервер не уходит.
    let pendingDelete = PendingDelete()

    private let cloud: CloudRepository
    private var didLoad = false

    init(cloud: CloudRepository) { self.cloud = cloud }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    /// Перезагрузка списка. Важно: НЕ поднимаем `isLoading` здесь — иначе при
    /// pull-to-refresh экран свернул бы `List` (носитель `.refreshable`) в
    /// полноэкранный `ProgressView`, SwiftUI отменил бы задачу обновления, и
    /// gRPC-запрос упал бы с «the transport threw an unexpected error».
    /// Спиннер первого показа обеспечивает дефолт `isLoading = true`, спиннер
    /// потягивания рисует сам `.refreshable`.
    func reload() async {
        // Если есть отложенное удаление навсегда — досдаём его, иначе сервер
        // вернёт нам обратно элемент, который пользователь уже визуально убрал.
        await pendingDelete.flushIfAny()
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

    /// Оптимистичное «удалить навсегда»: сразу убираем элемент из списка и кладём
    /// удаление в очередь — реальный запрос уйдёт, когда snackbar отсчитает
    /// 5 секунд (или пользователь поставит другое удаление в очередь).
    func deleteForever(_ item: TrashItem) {
        guard let index = state.items.firstIndex(where: { $0.id == item.id }) else { return }
        state.items.remove(at: index)
        pendingDelete.schedule(
            label: item.name,
            action: { [weak self, cloud] in
                do { try await cloud.deleteFromTrash(entryID: item.id) }
                catch {
                    // Сервер не дал — возвращаем элемент через reload и сообщаем.
                    self?.state.snackbar = domainErrorMessage(error)
                    await self?.reload()
                }
            },
            onUndo: { [weak self] in
                guard let self else { return }
                let position = min(index, state.items.count)
                state.items.insert(item, at: position)
            }
        )
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
