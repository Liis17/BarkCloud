import SwiftUI
import Photos
import UIKit
import BarkCloudKit

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
        /// URL созданной публичной ссылки → системный Share Sheet.
        var pendingShareURL: ShareableURL?

        fileprivate var cursorAddedAt: Date?
        fileprivate var cursorFileID: String = ""
    }

    var state: UiState

    /// Отложенное удаление файла из облака (контекстное меню) — snackbar внизу
    /// с отсчётом и кнопкой «Отменить».
    let pendingDelete = PendingDelete()

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
        // Досдаём отложенное удаление, иначе сервер вернёт нам убранный файл.
        await pendingDelete.flushIfAny()
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
                // Связь облако↔устройство для синхронного удаления.
                await CloudDeviceLinkStore.shared.link(fileID: id, localIdentifier: asset.localIdentifier)
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

    /// Скопировать временную ссылку на скачивание файла в буфер обмена.
    func copyLink(_ item: MediaItem) async {
        do {
            let urls = try await cloud.transfer.tempDownloadURLs(fileIDs: [item.id])
            guard let url = urls[item.id] else {
                state.snackbar = domainErrorMessage(CloudActionError.noLink); return
            }
            UIPasteboard.general.url = url
            state.snackbar = String(localized: "snack_link_copied")
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    /// Создать постоянную публичную ссылку и открыть системный Share Sheet.
    func makePublic(_ item: MediaItem) async {
        do {
            let link = try await cloud.createShare(fileID: item.id, name: item.fileName)
            guard let url = link.url else {
                state.snackbar = domainErrorMessage(CloudActionError.noLink); return
            }
            state.pendingShareURL = ShareableURL(url: url)
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    /// Сделать весь альбом публичным (`/al/{token}`) и открыть системный Share Sheet.
    func shareAlbum() async {
        do {
            let link = try await cloud.createAlbumShare(albumID: state.album.id, name: state.album.name)
            guard let url = link.url else {
                state.snackbar = domainErrorMessage(CloudActionError.noLink); return
            }
            state.pendingShareURL = ShareableURL(url: url)
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    /// Оптимистичное удаление файла из облака (в корзину) — не путать с «Убрать
    /// из альбома». Сразу убираем из сетки и кладём в очередь; реальный запрос
    /// уйдёт, когда snackbar отсчитает 5 секунд.
    func deleteFromCloud(item: MediaItem) {
        guard let index = state.items.firstIndex(where: { $0.id == item.id }) else { return }
        state.items.remove(at: index)
        pendingDelete.schedule(
            label: item.fileName,
            action: { [weak self, cloud] in
                do {
                    try await cloud.deleteUserMedia(fileID: item.id)
                    await DeviceCopyCleaner.deleteDeviceCopy(forCloudFileID: item.id)
                } catch {
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

    /// Добавить файл в другой существующий альбом.
    func addToAlbum(fileID: String, albumID: String) async {
        do {
            try await albums.addItems(albumID: albumID, fileIDs: [fileID])
            state.snackbar = String(localized: "media_added_to_album")
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    func createAlbumAndAdd(fileID: String) async {
        do {
            let name = "\(String(localized: "albums_create_title")) \(Self.randomSuffix())"
            let album = try await albums.createAlbum(name: name)
            try await albums.addItems(albumID: album.id, fileIDs: [fileID])
            state.snackbar = String(localized: "media_added_to_album")
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    private static func randomSuffix(_ length: Int = 5) -> String {
        let alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
        return String((0..<length).compactMap { _ in alphabet.randomElement() })
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
    @State private var propertiesTarget: FilePropertiesTarget?
    @State private var albumPickerItem: MediaItem?
    @State private var shareWithUserContext: ShareWithUserContext?

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
        .fullScreenCover(item: $selected) { item in
            if let vm {
                MediaPagerScreen(
                    ids: vm.state.items.map(\.id),
                    startIndex: vm.state.items.firstIndex(where: { $0.id == item.id }) ?? 0,
                    resolve: MediaPagerResolver.cloud(
                        transfer: env.fileTransfer,
                        cache: env.fileCache,
                        viewIDByFileID: MediaPagerResolver.jpegViewMap(vm.state.items)
                    ),
                    loadMore: {
                        guard let last = vm.state.items.last else { return vm.state.items.map(\.id) }
                        await vm.loadMoreIfNeeded(current: last)
                        return vm.state.items.map(\.id)
                    },
                    onClose: { selected = nil }
                )
            }
        }
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
        .sheet(item: $propertiesTarget) { FilePropertiesSheet(target: $0) }
        .sheet(item: $shareWithUserContext) { context in
            ShareWithUserSheet(context: context) { shareWithUserContext = nil }
        }
        .sharePresenter(url: Binding(
            get: { vm?.state.pendingShareURL },
            set: { vm?.state.pendingShareURL = $0 }
        ))
        .sheet(item: $albumPickerItem) { item in
            AlbumPickerSheet(
                albums: env.albumRepository,
                onPickExisting: { albumID in Task { await vm?.addToAlbum(fileID: item.id, albumID: albumID) } },
                onCreateNew: { Task { await vm?.createAlbumAndAdd(fileID: item.id) } }
            )
        }
        .overlay(alignment: .bottom) {
            if let vm { PendingDeleteSnackbar(store: vm.pendingDelete) }
        }
        .overlay(alignment: .bottom) { if let vm { snackbar(vm) } }
    }

    /// Пункты контекстного меню одного файла альбома (по удержанию ячейки).
    @ViewBuilder
    private func itemMenu(_ vm: AlbumDetailViewModel, _ item: MediaItem) -> some View {
        Button(String(localized: "ctx_properties")) {
            if let asset = item.asset { propertiesTarget = .cloud(asset) }
        }
        Button(String(localized: "ctx_copy_link")) {
            Task { await vm.copyLink(item) }
        }
        Button(String(localized: "ctx_make_public")) {
            Task { await vm.makePublic(item) }
        }
        Button(String(localized: "shared_with_user_action")) {
            shareWithUserContext = ShareWithUserContext(fileID: item.id, fileName: item.fileName)
        }
        Button(String(localized: "ctx_add_to_album")) {
            albumPickerItem = item
        }
        Button(String(localized: "albums_set_cover")) {
            Task { await vm.setCover(fileID: item.id) }
        }
        Button(String(localized: "albums_remove_item")) {
            Task { await vm.removeItem(fileID: item.id) }
        }
        Button(String(localized: "ctx_delete"), role: .destructive) {
            vm.deleteFromCloud(item: item)
        }
    }

    @ViewBuilder
    private func snackbar(_ vm: AlbumDetailViewModel) -> some View {
        if let text = vm.state.snackbar {
            Text(verbatim: text)
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurface)
                .padding(12)
                .background(.regularMaterial)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .padding(.bottom, 16)
                .onAppear {
                    Task { @MainActor in
                        try? await Task.sleep(nanoseconds: 2_000_000_000)
                        vm.snackbarShown()
                    }
                }
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
                        MediaThumb(fileId: item.id, previewWidth: item.previewWidth, thumbnailURL: item.thumbnailURL, isVideo: item.isVideo)
                            .onTapGesture { selected = item }
                            .onAppear { Task { await vm.loadMoreIfNeeded(current: item) } }
                            .shakeContextMenu { itemMenu(vm, item) }
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
                    Task { await vm?.shareAlbum() }
                } label: {
                    Label(String(localized: "albums_share"), systemImage: "square.and.arrow.up")
                }
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

    /// Фильтр кастомного пикера: по типу вкладки, либо фото+видео в режиме «Альбомы».
    private var pickerFilter: DeviceAssetPickerFilter {
        switch kind {
        case .video: return .video
        case .photo: return .photo
        case nil: return .any
        }
    }
}
