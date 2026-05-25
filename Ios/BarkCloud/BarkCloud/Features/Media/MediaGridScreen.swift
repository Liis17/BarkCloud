import SwiftUI
import PhotosUI

/// Сетка медиа в 3 столбика с квадратными ячейками. Используется вкладками
/// Фото и Видео: загружает реальные данные через `CloudApi.ListUserMedia`
/// (cursor-пагинация), показывает превью, открывает оригинал и грузит новые файлы.
struct MediaGridScreen: View {
    let kind: MediaKind

    @Environment(AppEnvironment.self) private var env
    @State private var vm: MediaGridViewModel?
    @State private var pickerItems: [PhotosPickerItem] = []
    @State private var selected: MediaItem?

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
        .toolbar { uploadButton }
        .task {
            if vm == nil {
                vm = MediaGridViewModel(kind: kind, cloud: env.cloudRepository)
            }
            await vm?.loadIfNeeded()
        }
        .onChange(of: pickerItems) { _, items in handlePick(items) }
        .fullScreenCover(item: $selected) { item in viewer(item) }
    }

    @ViewBuilder
    private func content(_ vm: MediaGridViewModel) -> some View {
        if !vm.state.isPlaceholder && vm.state.items.isEmpty {
            emptyState
        } else {
            grid(vm)
        }
    }

    private func grid(_ vm: MediaGridViewModel) -> some View {
        ScrollView {
            LazyVGrid(columns: columns, spacing: Self.spacing) {
                ForEach(vm.state.items) { item in
                    MediaThumb(thumbnailURL: item.thumbnailURL, isVideo: item.isVideo)
                        .onTapGesture {
                            if !vm.state.isPlaceholder { selected = item }
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
    private var uploadButton: some ToolbarContent {
        ToolbarItem(placement: .topBarTrailing) {
            if vm?.state.isUploading == true {
                ProgressView()
            } else {
                PhotosPicker(
                    selection: $pickerItems,
                    maxSelectionCount: 10,
                    matching: kind.isVideo ? .videos : .images
                ) {
                    Image(systemName: "plus")
                }
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

    private func handlePick(_ items: [PhotosPickerItem]) {
        guard !items.isEmpty else { return }
        Task {
            var files: [(data: Data, fileName: String)] = []
            for item in items {
                if let data = try? await item.loadTransferable(type: Data.self) {
                    let ext = item.supportedContentTypes.first?.preferredFilenameExtension
                        ?? (kind.isVideo ? "mp4" : "jpg")
                    files.append((data, "\(UUID().uuidString).\(ext)"))
                }
            }
            pickerItems = []
            await vm?.upload(files)
        }
    }
}
