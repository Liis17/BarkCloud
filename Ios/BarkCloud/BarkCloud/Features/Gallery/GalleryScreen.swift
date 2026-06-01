import SwiftUI
import UIKit
import Photos

/// Вкладка «Галерея»: медиатека устройства (фото+видео) сеткой 3×. Тап открывает
/// просмотр; режим выбора позволяет отметить ассеты и загрузить их в облако.
struct GalleryScreen: View {
    @Environment(AppEnvironment.self) private var env
    @Environment(\.openURL) private var openURL

    @State private var vm: GalleryViewModel?
    @State private var viewer: ViewerItem?
    @State private var showBackup = false
    @State private var propertiesTarget: FilePropertiesTarget?
    @State private var albumPickerAsset: PickerAsset?

    private struct ViewerItem: Identifiable {
        let id: String
        let asset: PHAsset
    }

    private struct PickerAsset: Identifiable {
        let id: String
        let asset: PHAsset
    }

    private static let spacing: CGFloat = 2
    private let columns = Array(repeating: GridItem(.flexible(), spacing: spacing), count: 3)

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(String(localized: "tab_gallery"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { toolbarContent }
        .task {
            if vm == nil { vm = GalleryViewModel(cloud: env.cloudRepository, albums: env.albumRepository) }
            await vm?.loadIfNeeded()
        }
        .onReceive(NotificationCenter.default.publisher(for: .galleryDidFocus)) { _ in
            vm?.reload()
        }
        .fullScreenCover(item: $viewer) { item in viewerScreen(item.asset) }
        .fullScreenCover(isPresented: $showBackup) {
            BackupSheet(onClose: { showBackup = false })
                .presentationBackground(.clear)
        }
        .sheet(item: $propertiesTarget) { FilePropertiesSheet(target: $0) }
        .sheet(item: Binding(
            get: { vm?.pendingShareWithUser },
            set: { vm?.pendingShareWithUser = $0 }
        )) { context in
            ShareWithUserSheet(context: context) { vm?.pendingShareWithUser = nil }
        }
        .sheet(item: Binding(
            get: { vm?.pendingShareURL },
            set: { vm?.pendingShareURL = $0 }
        )) { item in
            ActivityViewController(activityItems: [item.url])
        }
        .sheet(item: $albumPickerAsset) { picker in
            AlbumPickerSheet(
                albums: env.albumRepository,
                onPickExisting: { albumID in Task { await vm?.addToAlbum(asset: picker.asset, albumID: albumID) } },
                onCreateNew: { Task { await vm?.createAlbumAndAdd(asset: picker.asset) } }
            )
        }
        .overlay { if vm?.isUploading == true { uploadingOverlay(vm!) } }
    }

    @ViewBuilder
    private func content(_ vm: GalleryViewModel) -> some View {
        switch vm.access {
        case .undetermined:
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        case .denied:
            deniedState
        case .authorized, .limited:
            if vm.assets.isEmpty {
                emptyState
            } else {
                grid(vm)
            }
        }
    }

    private func grid(_ vm: GalleryViewModel) -> some View {
        ScrollView {
            LazyVGrid(columns: columns, spacing: Self.spacing) {
                ForEach(vm.assets, id: \.localIdentifier) { asset in
                    DeviceMediaThumb(
                        asset: asset,
                        isSelecting: vm.isSelecting,
                        isSelected: vm.isSelected(asset),
                        isInCloud: vm.cloudPresence[asset.localIdentifier] == true
                    )
                    .onTapGesture {
                        if vm.isSelecting {
                            vm.toggle(asset)
                        } else {
                            viewer = ViewerItem(id: asset.localIdentifier, asset: asset)
                        }
                    }
                    .shakeContextMenu(isActive: !vm.isSelecting) { itemMenu(vm, asset) }
                    .onAppear { vm.observeCloudPresence(for: asset) }
                }
            }
        }
        .safeAreaInset(edge: .bottom) { uploadBar(vm) }
        .overlay(alignment: .bottom) { snackbar(vm) }
    }

    @ViewBuilder
    private func uploadBar(_ vm: GalleryViewModel) -> some View {
        if vm.isSelecting && vm.hasSelection {
            Button {
                Task { await vm.uploadSelected() }
            } label: {
                Label(
                    "\(String(localized: "gallery_upload_selected")) (\(vm.selection.count))",
                    systemImage: "icloud.and.arrow.up"
                )
                .font(AppTypography.titleMedium)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 14)
                .background(AppColors.accent)
                .foregroundStyle(.white)
                .clipShape(RoundedRectangle(cornerRadius: 12))
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 8)
            .background(.regularMaterial)
        }
    }

    /// Пункты контекстного меню одного ассета устройства (по удержанию ячейки).
    @ViewBuilder
    private func itemMenu(_ vm: GalleryViewModel, _ asset: PHAsset) -> some View {
        Button(String(localized: "ctx_properties")) {
            propertiesTarget = .device(asset)
        }
        Button(String(localized: "ctx_copy_link")) {
            Task { await vm.copyLink(asset: asset) }
        }
        Button(String(localized: "ctx_make_public")) {
            Task { await vm.makePublic(asset: asset) }
        }
        Button(String(localized: "shared_with_user_action")) {
            Task { await vm.prepareShareWithUser(asset: asset) }
        }
        Button(String(localized: "ctx_add_to_album")) {
            albumPickerAsset = PickerAsset(id: asset.localIdentifier, asset: asset)
        }
        Button(String(localized: "ctx_delete_device"), role: .destructive) {
            Task { await vm.deleteFromDevice(asset: asset) }
        }
    }

    @ViewBuilder
    private func snackbar(_ vm: GalleryViewModel) -> some View {
        if let text = vm.snackbar {
            Text(verbatim: text)
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurface)
                .padding(12)
                .background(.regularMaterial)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .padding(.bottom, 24)
                .onAppear {
                    Task { @MainActor in
                        try? await Task.sleep(nanoseconds: 2_000_000_000)
                        vm.snackbarShown()
                    }
                }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 16) {
            Image(systemName: "photo.on.rectangle")
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text("gallery_empty")
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }

    private var deniedState: some View {
        VStack(spacing: 16) {
            Image(systemName: "lock.shield")
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text("gallery_access_denied")
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurface)
                .multilineTextAlignment(.center)
            Button(String(localized: "gallery_open_settings")) {
                if let url = URL(string: UIApplication.openSettingsURLString) {
                    openURL(url)
                }
            }
            .buttonStyle(.borderedProminent)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItem(placement: .topBarTrailing) {
            if let vm, vm.access == .authorized || vm.access == .limited, !vm.assets.isEmpty {
                Button(vm.isSelecting
                    ? String(localized: "action_cancel")
                    : String(localized: "gallery_select")
                ) {
                    vm.toggleSelecting()
                }
            }
        }
        ToolbarItem(placement: .topBarTrailing) {
            if let vm, vm.access == .authorized || vm.access == .limited {
                Button {
                    showBackup = true
                } label: {
                    backupToolbarIcon
                }
                .accessibilityLabel(String(localized: "backup_title"))
            }
        }
    }

    /// Иконка облака в тулбаре. Кольцевой прогресс рисуется только пока есть что
    /// загружать (`remainingCount > 0`). Когда очередь опустела — кольцо убирается,
    /// чтобы 100%-дуга не «висела» вокруг иконки до следующего открытия модалки.
    private var backupToolbarIcon: some View {
        let manager = env.backupManager
        let done = manager.uploadDone
        let failed = manager.uploadFailed
        let remaining = manager.remainingCount
        let total = done + failed + remaining
        let progress: Double = total > 0 ? min(1.0, Double(done) / Double(total)) : 0
        let showRing = manager.autoUploadEnabled && remaining > 0
        let ringSize: CGFloat = 28

        return ZStack {
            if showRing {
                Circle()
                    .stroke(AppColors.accent.opacity(0.25), lineWidth: 2)
                    .frame(width: ringSize, height: ringSize)
                Circle()
                    .trim(from: 0, to: progress)
                    .stroke(
                        AppColors.accent,
                        style: StrokeStyle(lineWidth: 2, lineCap: .round)
                    )
                    .rotationEffect(.degrees(-90))
                    .frame(width: ringSize, height: ringSize)
                    .animation(.easeOut(duration: 0.4), value: progress)
            }
            // Когда показано кольцо — иконку чуть уменьшаем, чтобы дуга на неё не налезала.
            Image(systemName: "icloud")
                .font(showRing ? .system(size: 15) : nil)
        }
        .frame(width: ringSize, height: ringSize)
    }

    private func uploadingOverlay(_ vm: GalleryViewModel) -> some View {
        ZStack {
            Color.black.opacity(0.25).ignoresSafeArea()
            VStack(spacing: 12) {
                ProgressView()
                Text(verbatim: "\(String(localized: "gallery_uploading")) \(vm.uploadDone)/\(vm.uploadTotal)")
                    .font(AppTypography.bodyMedium)
                    .foregroundStyle(AppColors.onSurface)
            }
            .padding(24)
            .background(.regularMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 16))
        }
    }

    private func viewerScreen(_ asset: PHAsset) -> some View {
        NavigationStack {
            DeviceMediaViewer(asset: asset)
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) {
                        Button(String(localized: "action_close")) { viewer = nil }
                    }
                }
        }
    }
}
