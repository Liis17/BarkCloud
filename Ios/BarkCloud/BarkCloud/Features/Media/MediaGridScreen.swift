import SwiftUI
import Photos

/// Сетка медиа в 3 столбика с квадратными ячейками. Используется вкладками
/// Фото и Видео: загружает реальные данные через `CloudApi.ListUserMedia`
/// (cursor-пагинация), показывает превью, открывает оригинал и грузит новые файлы.
/// Кнопка «Выбрать» включает мультивыбор с действиями «Удалить» и
/// «Добавить в альбом» в нижней панели.
struct MediaGridScreen: View {
    let kind: MediaKind

    @Environment(AppEnvironment.self) private var env
    @State private var vm: MediaGridViewModel?
    @State private var showPicker = false
    @State private var showAlbumPicker = false
    @State private var showDeleteConfirm = false
    @State private var selected: MediaItem?
    @State private var propertiesTarget: FilePropertiesTarget?
    @State private var albumPickerItem: MediaItem?
    @State private var shareWithUserContext: ShareWithUserContext?

    private static let columnCount = 3
    private static let spacing: CGFloat = 2

    private let columns = Array(
        repeating: GridItem(.flexible(), spacing: spacing),
        count: columnCount
    )

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .toolbar { toolbarContent }
        .safeAreaInset(edge: .bottom) {
            if let vm, vm.state.isSelecting {
                actionBar(vm)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.spring(response: 0.35, dampingFraction: 0.85), value: vm?.state.isSelecting ?? false)
        .overlay(alignment: .bottom) {
            if let vm { PendingDeleteSnackbar(store: vm.pendingDelete) }
        }
        .overlay(alignment: .bottom) { if let vm { snackbar(vm) } }
        .task {
            if vm == nil {
                vm = MediaGridViewModel(kind: kind, cloud: env.cloudRepository, albums: env.albumRepository, vault: env.vault)
            }
            await vm?.loadIfNeeded()
        }
        // Автозагрузка медиатеки завершилась (баннер прогресса погас) — подтянуть
        // свежезагруженные медиа в сетку, пока вкладка открыта, без ручного refresh.
        .onChange(of: env.uploadProgress.isActive) { wasActive, isActive in
            if wasActive, !isActive, env.uploadProgress.currentSource == .backup {
                Task { await vm?.reload() }
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
        .sheet(isPresented: $showPicker) {
            DeviceAssetPickerScreen(
                filter: kind.isVideo ? .video : .photo,
                confirmTitle: String(localized: "gallery_upload_selected"),
                blockAlreadyUploaded: true
            ) { assets in
                Task { await vm?.uploadAssets(assets) }
            }
        }
        .sheet(isPresented: $showAlbumPicker) {
            AlbumPickerSheet(
                albums: env.albumRepository,
                onPickExisting: { albumID in Task { await vm?.addSelectedToAlbum(albumID: albumID) } },
                onCreateNew: { Task { await vm?.createAlbumAndAddSelected() } }
            )
        }
        .sheet(item: $propertiesTarget) { FilePropertiesSheet(target: $0) }
        .sheet(item: $shareWithUserContext) { context in
            ShareWithUserSheet(context: context) { shareWithUserContext = nil }
        }
        .sheet(item: Binding(
            get: { vm?.state.pendingShareURL },
            set: { vm?.state.pendingShareURL = $0 }
        )) { item in
            ActivityViewController(activityItems: [item.url])
        }
        .sheet(item: $albumPickerItem) { item in
            AlbumPickerSheet(
                albums: env.albumRepository,
                onPickExisting: { albumID in Task { await vm?.addToAlbum(fileID: item.id, albumID: albumID) } },
                onCreateNew: { Task { await vm?.createAlbumAndAdd(fileID: item.id) } }
            )
        }
    }

    /// Пункты контекстного меню одного файла (по удержанию ячейки).
    @ViewBuilder
    private func itemMenu(_ vm: MediaGridViewModel, _ item: MediaItem) -> some View {
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
        Button(String(localized: "ctx_delete"), role: .destructive) {
            vm.deleteSingle(item)
        }
    }

    @ViewBuilder
    private func content(_ vm: MediaGridViewModel) -> some View {
        ScrollView {
            if !vm.state.isPlaceholder && vm.state.items.isEmpty {
                emptyState
                    .containerRelativeFrame(.vertical)
            } else {
                LazyVGrid(columns: columns, spacing: Self.spacing) {
                    ForEach(vm.state.items) { item in
                        MediaThumb(
                            fileId: item.id,
                            previewWidth: item.previewWidth,
                            thumbnailURL: item.thumbnailURL,
                            isVideo: item.isVideo,
                            isSelecting: vm.state.isSelecting,
                            isSelected: vm.state.selection.contains(item.id)
                        )
                        .onTapGesture {
                            if vm.state.isSelecting {
                                vm.toggleSelection(item)
                            } else if !vm.state.isPlaceholder {
                                selected = item
                            }
                        }
                        .shakeContextMenu(isActive: !vm.state.isSelecting && !vm.state.isPlaceholder) {
                            itemMenu(vm, item)
                        }
                        .onAppear {
                            Task { await vm.loadMoreIfNeeded(current: item) }
                        }
                    }
                }
                .redacted(reason: vm.state.isPlaceholder ? .placeholder : [])

                if vm.state.isLoadingMore {
                    ProgressView().padding()
                }
            }
        }
        // Потянуть вниз — перезагрузить список (фото/видео).
        .barkRefreshable { await vm.reload() }
    }

    private var emptyState: some View {
        VStack(spacing: 16) {
            Image(systemName: kind.emptyIcon)
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text(kind.emptyKey)
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        if let vm, vm.state.isSelecting {
            ToolbarItem(placement: .topBarTrailing) {
                Button(String(localized: "action_cancel")) { vm.exitSelection() }
            }
        } else if vm?.state.isUploading == true {
            ToolbarItem(placement: .topBarTrailing) { ProgressView() }
        } else {
            ToolbarItem(placement: .topBarTrailing) {
                Button(String(localized: "gallery_select")) { vm?.enterSelection() }
                    .disabled(vm?.state.isPlaceholder ?? true)
            }
            ToolbarItem(placement: .topBarTrailing) {
                Button { showPicker = true } label: { Image(systemName: "plus") }
            }
        }
    }

    private func actionBar(_ vm: MediaGridViewModel) -> some View {
        VStack(spacing: 8) {
            if vm.state.isProcessing {
                deleteProgress(vm)
            } else {
                selectionActions(vm)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .animation(.easeInOut(duration: 0.2), value: vm.state.isProcessing)
    }

    /// Прогресс последовательного удаления (заменяет кнопки на время операции).
    @ViewBuilder
    private func deleteProgress(_ vm: MediaGridViewModel) -> some View {
        VStack(spacing: 6) {
            Text(verbatim: "\(String(localized: "media_deleting")) \(vm.state.deleteDone)/\(vm.state.deleteTotal)")
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
            ProgressView(value: Double(vm.state.deleteDone), total: Double(max(vm.state.deleteTotal, 1)))
                .tint(AppColors.accent)
        }
        .transition(.opacity)
    }

    /// Счётчик выбранного + кнопки действий (Удалить / Добавить в альбом).
    @ViewBuilder
    private func selectionActions(_ vm: MediaGridViewModel) -> some View {
        Text(String(format: NSLocalizedString("media_selected_count", comment: ""),
                    vm.state.selection.count))
            .font(AppTypography.bodySmall)
            .foregroundStyle(AppColors.onSurfaceVariant)
        HStack(spacing: 12) {
            Button(role: .destructive) {
                showDeleteConfirm = true
            } label: {
                Label("action_delete", systemImage: "trash")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(AppColors.error)
            // Подтверждение удаления — поповером прямо над кнопкой «Удалить».
            .popover(isPresented: $showDeleteConfirm, arrowEdge: .bottom) {
                deleteConfirm(vm)
            }

            Button {
                showAlbumPicker = true
            } label: {
                Label("media_add_to_album", systemImage: "rectangle.stack.badge.plus")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(AppColors.accent)
        }
        .controlSize(.large)
        .disabled(!vm.hasSelection)
        .transition(.opacity)

        // Переместить выбранное в локальный сейф (скрыть из галереи под Face ID).
        Button {
            vm.moveSelectedToVault()
        } label: {
            Label("vault_move_here", systemImage: "lock.fill")
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(.bordered)
        .tint(AppColors.accent)
        .controlSize(.large)
        .disabled(!vm.hasSelection)
        .transition(.opacity)
    }

    /// Поповер подтверждения удаления, привязанный к кнопке «Удалить».
    private func deleteConfirm(_ vm: MediaGridViewModel) -> some View {
        VStack(spacing: 14) {
            Text(String(format: NSLocalizedString("media_delete_message", comment: ""),
                        vm.state.selection.count))
                .font(AppTypography.bodyMedium)
                .foregroundStyle(AppColors.onSurface)
                .multilineTextAlignment(.center)
            Button(role: .destructive) {
                showDeleteConfirm = false
                Task { await vm.deleteSelected() }
            } label: {
                Label("action_delete", systemImage: "trash")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(AppColors.error)
            .controlSize(.large)
        }
        .padding(16)
        .frame(width: 240)
        .presentationCompactAdaptation(.popover)
    }

    @ViewBuilder
    private func snackbar(_ vm: MediaGridViewModel) -> some View {
        if let text = vm.state.snackbar {
            Text(verbatim: text)
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurface)
                .padding(12)
                .background(.regularMaterial)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .padding(.bottom, vm.state.isSelecting ? 92 : 16)
                .onAppear {
                    Task { @MainActor in
                        try? await Task.sleep(nanoseconds: 2_000_000_000)
                        vm.snackbarShown()
                    }
                }
        }
    }

}
