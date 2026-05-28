import SwiftUI

/// Сетка карточек альбомов. `kind` определяет фильтр содержимого при открытии
/// альбома: `nil` — показывать и фото, и видео (режим «Альбомы»).
struct AlbumsGridScreen: View {
    let kind: MediaKind?

    @Environment(AppEnvironment.self) private var env
    @State private var vm: AlbumsViewModel?
    @State private var showCreate = false
    @State private var newName = ""

    private static let columns = Array(repeating: GridItem(.flexible(), spacing: 12), count: 2)

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { newName = ""; showCreate = true } label: { Image(systemName: "plus") }
            }
        }
        .task {
            if vm == nil { vm = AlbumsViewModel(albums: env.albumRepository) }
            await vm?.loadIfNeeded()
        }
        .alert(String(localized: "albums_create_title"), isPresented: $showCreate) {
            TextField(String(localized: "albums_name_placeholder"), text: $newName)
            Button(String(localized: "action_create")) {
                Task { await vm?.create(name: newName) }
            }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
    }

    @ViewBuilder
    private func content(_ vm: AlbumsViewModel) -> some View {
        if vm.state.isLoading {
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if vm.state.albums.isEmpty {
            ScrollView {
                VStack(spacing: 16) {
                    Image(systemName: "rectangle.stack")
                        .font(.system(size: 56))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    Text("albums_list_empty")
                        .font(AppTypography.titleMedium)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .frame(maxWidth: .infinity)
                .containerRelativeFrame(.vertical)
            }
            .barkRefreshable { await vm.reload(showSpinner: false) }
        } else {
            ScrollView {
                LazyVGrid(columns: Self.columns, spacing: 12) {
                    ForEach(vm.state.albums) { album in
                        NavigationLink {
                            AlbumDetailScreen(album: album, kind: kind)
                        } label: {
                            AlbumCardView(album: album)
                        }
                        .buttonStyle(.plain)
                        .onAppear { Task { await vm.loadMoreIfNeeded(current: album) } }
                    }
                }
                .padding(16)
                if vm.state.isLoadingMore { ProgressView().padding() }
            }
            // Потянуть вниз — перезагрузить список альбомов.
            .barkRefreshable { await vm.reload(showSpinner: false) }
        }
    }
}

private struct AlbumCardView: View {
    let album: AlbumCard

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            SquareThumbClip(cornerRadius: 10) {
                if let url = album.coverPreviewURL {
                    RemoteImage(url: url, contentMode: .fill) {
                        AppColors.onSurface.opacity(0.08)
                    }
                } else {
                    AppColors.onSurface.opacity(0.08)
                        .overlay {
                            Image(systemName: "photo.stack")
                                .font(.system(size: 36))
                                .foregroundStyle(AppColors.onSurfaceVariant)
                        }
                }
            }
            Text(verbatim: album.name)
                .font(AppTypography.titleMedium)
                .lineLimit(1)
            Text(verbatim: FormatUtils.formatChildCount(album.itemsCount))
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
        }
    }
}
