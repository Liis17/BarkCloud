import Foundation
import Observation
import BarkCloudKit

/// Содержимое умной папки: файлы по критериям с cursor-пагинацией. Рендер (сетка
/// или список) определяется `viewMode` папки в экране.
@MainActor
@Observable
final class SmartFolderDetailViewModel {
    var items: [MediaItem] = []
    var isLoading = true
    var isLoadingMore = false
    var snackbar: String?

    private var canLoadMore = false
    private var cursorCreatedAt: Date?
    private var cursorFileID = ""

    private let folderID: String
    private let repo: DynamicFolderRepository
    private var didLoad = false

    init(folderID: String, repo: DynamicFolderRepository) {
        self.folderID = folderID
        self.repo = repo
    }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    func reload(showSpinner: Bool = true) async {
        if showSpinner { isLoading = true }
        do {
            let page = try await repo.listItems(folderID: folderID, limit: 60)
            items = page.items.map(MediaItem.init(asset:))
            cursorCreatedAt = page.nextCursorCreatedAt
            cursorFileID = page.nextCursorFileID
            canLoadMore = page.hasMore
        } catch {
            items = []
            snackbar = domainErrorMessage(error)
        }
        isLoading = false
    }

    func loadMoreIfNeeded(current item: MediaItem) async {
        guard canLoadMore, !isLoadingMore, item.id == items.last?.id else { return }
        isLoadingMore = true
        do {
            let page = try await repo.listItems(
                folderID: folderID, limit: 60,
                cursorCreatedAt: cursorCreatedAt, cursorFileID: cursorFileID
            )
            items.append(contentsOf: page.items.map(MediaItem.init(asset:)))
            cursorCreatedAt = page.nextCursorCreatedAt
            cursorFileID = page.nextCursorFileID
            canLoadMore = page.hasMore
        } catch {
            snackbar = domainErrorMessage(error)
        }
        isLoadingMore = false
    }

    func snackbarShown() { snackbar = nil }
}
