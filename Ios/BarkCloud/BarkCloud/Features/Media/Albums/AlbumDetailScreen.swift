import SwiftUI
import Photos

@MainActor
@Observable
final class AlbumDetailViewModel {
    struct UiState {
        var album: AlbumCard
        var items: [MediaItem] = []
        var isLoading = true
        var isLoadingMore = false
        var canLoadMore = false
        var isUploading = false
        var snackbar: String?

        fileprivate var cursorAddedAt: Date?
        fileprivate var cursorFileID: String = ""
    }

    var state: UiState

    private let albums: AlbumRepository
    private let cloud: CloudRepository
    private let kind: MediaKind?
    private var didLoad = false

    init(album: AlbumCard, kind: MediaKind?, albums: AlbumRepository, cloud: CloudRepository) {
        self.state = UiState(album: album)
        self.albums = albums
        self.cloud = cloud
        self.kind = kind
    }

    /// `nil` — без фильтра по типу (показывать и фото, и видео).
    private var apiKind: CloudMediaKind? {
        guard let kind else { return nil }
        return kind.isVideo ? .video : .photo
    }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    func reload() async {
        state.isLoading = true
        do {
            let page = try await albums.listItems(albumID: state.album.id, kindFilter: apiKind, limit: 60)
            state.items = page.items.map(MediaItem.init(asset:))
            state.cursorAddedAt = page.nextAddedAt
            state.cursorFileID = page.nextFileID
            state.canLoadMore = page.nextAddedAt != nil
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoading = false
    }

    func loadMoreIfNeeded(current item: MediaItem) async {
        guard state.canLoadMore, !state.isLoadingMore, item.id == state.items.last?.id else { return }
        state.isLoadingMore = true
        do {
            let page = try await albums.listItems(
                albumID: state.album.id, kindFilter: apiKind, limit: 60,
                cursorAddedAt: state.cursorAddedAt, cursorFileID: state.cursorFileID
            )
            state.items.append(contentsOf: page.items.map(MediaItem.init(asset:)))
            state.cursorAddedAt = page.nextAddedAt
            state.cursorFileID = page.nextFileID
            state.canLoadMore = page.nextAddedAt != nil
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoadingMore = false
    }

    /// Загрузить выбранные в кастомном пикере ассеты устройства и добавить их в
    /// альбом. Читаем оригинал через `DeviceAssetResource`; `uploadFile` дедуплицирует
    /// блоб по хешу и возвращает существующий `file_id`, так что «уже загруженное»
    /// фото можно добавить в альбом без повторной заливки.
    func uploadAndAddAssets(_ assets: [PHAsset]) async {
        guard !assets.isEmpty else { return }
        state.isUploading = true
        var fileIDs: [String] = []
        for asset in assets {
            if let pair = try? await DeviceAssetResource.originalData(for: asset),
               let id = try? await cloud.uploadFile(data: pair.0, fileName: pair.1) {
                fileIDs.append(id)
            }
        }
        do {
            if !fileIDs.isEmpty {
                try await albums.addItems(albumID: state.album.id, fileIDs: fileIDs)
            }
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isUploading = false
        await reload()
    }

    func setCover(fileID: String) async {
        do {
            state.album = try await albums.updateAlbum(albumID: state.album.id, coverFileID: fileID)
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    func removeItem(fileID: String) async {
        do {
            try await albums.removeItems(albumID: state.album.id, fileIDs: [fileID])
            await reload()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    func rename(name: String) async {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return }
        do {
            state.album = try await albums.updateAlbum(albumID: state.album.id, name: trimmed)
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    func delete() async -> Bool {
        do {
            try await albums.deleteAlbum(state.album.id)
            return true
        } catch {
            state.snackbar = domainErrorMessage(error)
            return false
        }
    }

    func snackbarShown() { state.snackbar = nil }
}

struct AlbumDetailScreen: View {
    let album: AlbumCard
    let kind: MediaKind?

    @Environment(AppEnvironment.self) private var env
    @Environment(\.dismiss) private var dismiss

    @State private var vm: AlbumDetailViewModel?
    @State private var showPicker = false
    @State private var selected: MediaItem?
    @State private var showRename = false
    @State private var renameText = ""
    @State private var showDelete = false

    private static let columns = Array(repeating: GridItem(.flexible(), spacing: 2), count: 3)

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView()
            }
        }
        .navigationTitle(vm?.state.album.name ?? album.name)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { toolbarContent }
        .task {
            if vm == nil {
                vm = AlbumDetailViewModel(album: album, kind: kind, albums: env.albumRepository, cloud: env.cloudRepository)
            }
            await vm?.loadIfNeeded()
        }
        .sheet(isPresented: $showPicker) {
            DeviceAssetPickerScreen(
                filter: pickerFilter,
                confirmTitle: String(localized: "albums_add"),
                blockAlreadyUploaded: false
            ) { assets in
                Task { await vm?.uploadAndAddAssets(assets) }
            }
        }
        .fullScreenCover(item: $selected) { item in viewer(item) }
        .alert(String(localized: "albums_rename_title"), isPresented: $showRename) {
            TextField(String(localized: "albums_name_placeholder"), text: $renameText)
            Button(String(localized: "action_save")) {
                Task { await vm?.rename(name: renameText) }
            }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
        .confirmationDialog(String(localized: "albums_delete_confirm"), isPresented: $showDelete, titleVisibility: .visible) {
            Button(String(localized: "action_delete"), role: .destructive) {
                Task { if await vm?.delete() == true { dismiss() } }
            }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
    }

    @ViewBuilder
    private func content(_ vm: AlbumDetailViewModel) -> some View {
        if vm.state.isLoading {
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if vm.state.items.isEmpty {
            VStack(spacing: 16) {
                Image(systemName: "photo.on.rectangle.angled")
                    .font(.system(size: 56))
                    .foregroundStyle(AppColors.onSurfaceVariant)
                Text("albums_empty")
                    .font(AppTypography.titleMedium)
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollView {
                LazyVGrid(columns: Self.columns, spacing: 2) {
                    ForEach(vm.state.items) { item in
                        MediaThumb(thumbnailURL: item.thumbnailURL, isVideo: item.isVideo)
                            .onTapGesture { selected = item }
                            .onAppear { Task { await vm.loadMoreIfNeeded(current: item) } }
                            .contextMenu {
                                Button {
                                    Task { await vm.setCover(fileID: item.id) }
                                } label: {
                                    Label(String(localized: "albums_set_cover"), systemImage: "star")
                                }
                                Button(role: .destructive) {
                                    Task { await vm.removeItem(fileID: item.id) }
                                } label: {
                                    Label(String(localized: "albums_remove_item"), systemImage: "minus.circle")
                                }
                            }
                    }
                }
                if vm.state.isLoadingMore { ProgressView().padding() }
            }
        }
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItem(placement: .topBarTrailing) {
            if vm?.state.isUploading == true {
                ProgressView()
            } else {
                Button {
                    showPicker = true
                } label: {
                    Image(systemName: "plus")
                }
            }
        }
        ToolbarItem(placement: .topBarTrailing) {
            Menu {
                Button {
                    renameText = vm?.state.album.name ?? ""
                    showRename = true
                } label: {
                    Label(String(localized: "action_rename"), systemImage: "pencil")
                }
                Button(role: .destructive) {
                    showDelete = true
                } label: {
                    Label(String(localized: "albums_delete"), systemImage: "trash")
                }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
        }
    }

    private func viewer(_ item: MediaItem) -> some View {
        NavigationStack {
            RemoteFilePreviewScreen(fileID: item.id, fileName: item.fileName, transfer: env.fileTransfer)
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) {
                        Button(String(localized: "action_close")) { selected = nil }
                    }
                }
        }
    }

    /// Фильтр кастомного пикера: по типу вкладки, либо фото+видео в режиме «Альбомы».
    private var pickerFilter: DeviceAssetPickerFilter {
        switch kind {
        case .video: return .video
        case .photo: return .photo
        case nil: return .any
        }
    }
}
