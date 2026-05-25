import Foundation
import Observation

struct MediaGridUiState {
    var items: [MediaItem] = []
    /// Пока `true` — сетка рисуется скелетонами (.redacted).
    var isPlaceholder: Bool = true
    var isLoadingMore: Bool = false
    var canLoadMore: Bool = false
    var isUploading: Bool = false
    var snackbar: String?

    fileprivate var cursorCreatedAt: Date?
    fileprivate var cursorFileID: String = ""
}

@MainActor
@Observable
final class MediaGridViewModel {
    var state: MediaGridUiState

    private let kind: MediaKind
    private let cloud: CloudRepository
    private var didLoad = false

    init(kind: MediaKind, cloud: CloudRepository) {
        self.kind = kind
        self.cloud = cloud
        self.state = MediaGridUiState(
            items: MediaItem.placeholders(count: 12, isVideo: kind.isVideo),
            isPlaceholder: true
        )
    }

    private var apiKind: CloudMediaKind { kind.isVideo ? .video : .photo }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    func reload() async {
        do {
            let page = try await cloud.listUserMedia(kind: apiKind, limit: 60)
            state.items = page.items.map(MediaItem.init(asset:))
            state.cursorCreatedAt = page.nextCursorCreatedAt
            state.cursorFileID = page.nextCursorFileID
            state.canLoadMore = page.hasMore
        } catch {
            state.items = []
            state.snackbar = domainErrorMessage(error)
        }
        state.isPlaceholder = false
    }

    func loadMoreIfNeeded(current item: MediaItem) async {
        guard state.canLoadMore, !state.isLoadingMore, !state.isPlaceholder,
              item.id == state.items.last?.id else { return }
        state.isLoadingMore = true
        do {
            let page = try await cloud.listUserMedia(
                kind: apiKind, limit: 60,
                cursorCreatedAt: state.cursorCreatedAt, cursorFileID: state.cursorFileID
            )
            state.items.append(contentsOf: page.items.map(MediaItem.init(asset:)))
            state.cursorCreatedAt = page.nextCursorCreatedAt
            state.cursorFileID = page.nextCursorFileID
            state.canLoadMore = page.hasMore
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoadingMore = false
    }

    func upload(_ files: [(data: Data, fileName: String)]) async {
        guard !files.isEmpty else { return }
        state.isUploading = true
        var anyFailed = false
        for file in files {
            do {
                _ = try await cloud.uploadFile(data: file.data, fileName: file.fileName)
            } catch {
                anyFailed = true
            }
        }
        state.isUploading = false
        if anyFailed { state.snackbar = String(localized: "upload_failed") }
        await reload()
    }

    func snackbarShown() { state.snackbar = nil }
}
