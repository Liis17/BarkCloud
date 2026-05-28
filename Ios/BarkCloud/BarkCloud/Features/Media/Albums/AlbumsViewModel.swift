import Foundation
import Observation

@MainActor
@Observable
final class AlbumsViewModel {
    struct UiState {
        var albums: [AlbumCard] = []
        var isLoading = true
        var isLoadingMore = false
        var canLoadMore = false
        var snackbar: String?

        fileprivate var cursorUpdatedAt: Date?
        fileprivate var cursorAlbumID: String = ""
    }

    var state = UiState()

    private let albums: AlbumRepository
    private var didLoad = false

    init(albums: AlbumRepository) { self.albums = albums }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    /// Перезагрузка списка. `showSpinner` поднимает полноэкранный `isLoading`
    /// (первый показ / после создания). При pull-to-refresh передаём `false` —
    /// иначе ветка `if isLoading` свернёт `ScrollView` (носитель `.refreshable`)
    /// в `ProgressView`, SwiftUI отменит задачу обновления, и gRPC-запрос упадёт
    /// с «the transport threw an unexpected error».
    func reload(showSpinner: Bool = true) async {
        if showSpinner { state.isLoading = true }
        do {
            let page = try await albums.listAlbums(limit: 50)
            state.albums = page.albums
            state.cursorUpdatedAt = page.nextCursorUpdatedAt
            state.cursorAlbumID = page.nextCursorAlbumID
            state.canLoadMore = page.hasMore
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoading = false
    }

    func loadMoreIfNeeded(current album: AlbumCard) async {
        guard state.canLoadMore, !state.isLoadingMore, album.id == state.albums.last?.id else { return }
        state.isLoadingMore = true
        do {
            let page = try await albums.listAlbums(
                limit: 50, cursorUpdatedAt: state.cursorUpdatedAt, cursorAlbumID: state.cursorAlbumID
            )
            state.albums.append(contentsOf: page.albums)
            state.cursorUpdatedAt = page.nextCursorUpdatedAt
            state.cursorAlbumID = page.nextCursorAlbumID
            state.canLoadMore = page.hasMore
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoadingMore = false
    }

    func create(name: String) async {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return }
        do {
            _ = try await albums.createAlbum(name: trimmed)
            await reload()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    func snackbarShown() { state.snackbar = nil }
}
