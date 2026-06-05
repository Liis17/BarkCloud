import SwiftUI
import BarkCloudKit

/// Просмотр содержимого умной папки. Рендер по `folder.viewMode`: сетка превью
/// (как вкладки Фото/Видео) или список строк (для документов/аудио). У
/// пользовательских папок в тулбаре — «Изменить» и «Удалить»; системные только
/// читаются.
struct SmartFolderDetailScreen: View {
    @Environment(AppEnvironment.self) private var env
    @Environment(\.dismiss) private var dismiss

    @State private var folder: DynamicFolderCard
    @State private var vm: SmartFolderDetailViewModel?
    @State private var selected: MediaItem?
    @State private var showEdit = false
    @State private var showDeleteConfirm = false

    /// Вызывается после изменения/удаления папки — родитель перезагружает список.
    let onChanged: () -> Void

    private let repo: DynamicFolderRepository

    init(folder: DynamicFolderCard, repo: DynamicFolderRepository, onChanged: @escaping () -> Void) {
        _folder = State(initialValue: folder)
        self.repo = repo
        self.onChanged = onChanged
    }

    private static let gridColumns = Array(
        repeating: GridItem(.flexible(), spacing: 2), count: 3
    )

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(folder.name)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { toolbarContent }
        .task {
            if vm == nil { vm = SmartFolderDetailViewModel(folderID: folder.id, repo: repo) }
            await vm?.loadIfNeeded()
        }
        .fullScreenCover(item: $selected) { item in
            if let vm {
                MediaPagerScreen(
                    ids: vm.items.map(\.id),
                    startIndex: vm.items.firstIndex(where: { $0.id == item.id }) ?? 0,
                    resolve: MediaPagerResolver.cloud(
                        transfer: env.fileTransfer,
                        cache: env.fileCache,
                        viewIDByFileID: MediaPagerResolver.jpegViewMap(vm.items)
                    ),
                    loadMore: {
                        guard let last = vm.items.last else { return vm.items.map(\.id) }
                        await vm.loadMoreIfNeeded(current: last)
                        return vm.items.map(\.id)
                    },
                    onClose: { selected = nil }
                )
            }
        }
        .sheet(isPresented: $showEdit) {
            SmartFolderFormScreen(repo: repo, existing: folder) { updated in
                folder = updated
                onChanged()
                Task { await vm?.reload() }
            }
        }
        .confirmationDialog(
            String(localized: "smart_folder_delete_confirm"),
            isPresented: $showDeleteConfirm,
            titleVisibility: .visible
        ) {
            Button(String(localized: "action_delete"), role: .destructive) {
                Task {
                    try? await repo.delete(folderID: folder.id)
                    onChanged()
                    dismiss()
                }
            }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
        .overlay(alignment: .bottom) { if let vm { snackbar(vm) } }
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        if !folder.isSystem {
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Button {
                        showEdit = true
                    } label: {
                        Label("action_edit", systemImage: "pencil")
                    }
                    Button(role: .destructive) {
                        showDeleteConfirm = true
                    } label: {
                        Label("action_delete", systemImage: "trash")
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
            }
        }
    }

    @ViewBuilder
    private func content(_ vm: SmartFolderDetailViewModel) -> some View {
        if vm.isLoading {
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if vm.items.isEmpty {
            ScrollView {
                VStack(spacing: 16) {
                    Image(systemName: "line.3.horizontal.decrease.circle")
                        .font(.system(size: 56))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    Text("smart_folder_empty")
                        .font(AppTypography.titleMedium)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .frame(maxWidth: .infinity)
                .containerRelativeFrame(.vertical)
            }
            .barkRefreshable { await vm.reload(showSpinner: false) }
        } else if folder.viewMode == .dfViewList {
            listContent(vm)
        } else {
            gridContent(vm)
        }
    }

    private func gridContent(_ vm: SmartFolderDetailViewModel) -> some View {
        ScrollView {
            LazyVGrid(columns: Self.gridColumns, spacing: 2) {
                ForEach(vm.items) { item in
                    MediaThumb(
                        fileId: item.id,
                        previewWidth: item.previewWidth,
                        thumbnailURL: item.thumbnailURL,
                        isVideo: item.isVideo,
                        isSelecting: false,
                        isSelected: false
                    )
                    .onTapGesture { selected = item }
                    .onAppear { Task { await vm.loadMoreIfNeeded(current: item) } }
                }
            }
            if vm.isLoadingMore { ProgressView().padding() }
        }
        .barkRefreshable { await vm.reload(showSpinner: false) }
    }

    private func listContent(_ vm: SmartFolderDetailViewModel) -> some View {
        List {
            ForEach(vm.items) { item in
                Button {
                    selected = item
                } label: {
                    row(item)
                }
                .buttonStyle(.plain)
                .onAppear { Task { await vm.loadMoreIfNeeded(current: item) } }
            }
            if vm.isLoadingMore {
                ProgressView().frame(maxWidth: .infinity)
            }
        }
        .listStyle(.plain)
        .barkRefreshable { await vm.reload(showSpinner: false) }
    }

    @ViewBuilder
    private func row(_ item: MediaItem) -> some View {
        HStack(spacing: 12) {
            Image(systemName: item.isVideo ? "video.fill" : (item.asset?.kind == .document ? "doc.fill" : "photo.fill"))
                .font(.system(size: 20))
                .frame(width: 36, height: 36)
                .foregroundStyle(AppColors.accent)
                .background(AppColors.accent.opacity(0.12))
                .clipShape(RoundedRectangle(cornerRadius: 8))
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: item.fileName)
                    .font(AppTypography.bodyMedium)
                    .lineLimit(1)
                if let asset = item.asset {
                    Text(verbatim: "\(FormatUtils.formatSize(asset.fileSize)) · \(FormatUtils.formatDate(asset.createdAt))")
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
            }
            Spacer()
        }
        .padding(.vertical, 4)
        .contentShape(Rectangle())
    }

    @ViewBuilder
    private func snackbar(_ vm: SmartFolderDetailViewModel) -> some View {
        if let text = vm.snackbar {
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
}
