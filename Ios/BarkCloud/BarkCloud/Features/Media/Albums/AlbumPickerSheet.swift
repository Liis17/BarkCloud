import SwiftUI
import BarkCloudKit

/// Модалка выбора альбома для добавления выбранных медиа. Первым пунктом —
/// «Создать новый альбом» (создаётся «Новый альбом XXXXX» и в него кладутся
/// выбранные медиа), далее — существующие альбомы. Сама загружает список через
/// `AlbumRepository`; выбор пробрасывается наверх колбэками и закрывает лист.
struct AlbumPickerSheet: View {
    let albums: AlbumRepository
    let onPickExisting: (String) -> Void
    let onCreateNew: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var items: [AlbumCard] = []
    @State private var isLoading = true
    @State private var errorText: String?

    var body: some View {
        NavigationStack {
            List {
                Button {
                    onCreateNew()
                    dismiss()
                } label: {
                    Label(String(localized: "album_picker_create_new"),
                          systemImage: "rectangle.stack.badge.plus")
                        .font(AppTypography.bodyLarge)
                        .foregroundStyle(AppColors.accent)
                }

                ForEach(items) { album in
                    Button {
                        onPickExisting(album.id)
                        dismiss()
                    } label: {
                        albumRow(album)
                    }
                    .buttonStyle(.plain)
                }
            }
            .listStyle(.plain)
            .overlay {
                if isLoading {
                    ProgressView()
                } else if let errorText {
                    Text(verbatim: errorText)
                        .font(AppTypography.bodyMedium)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                        .padding()
                }
            }
            .navigationTitle(String(localized: "album_picker_title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button(String(localized: "action_cancel")) { dismiss() }
                }
            }
            .task { await load() }
        }
    }

    @ViewBuilder
    private func albumRow(_ album: AlbumCard) -> some View {
        HStack(spacing: 12) {
            cover(album)
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: album.name)
                    .font(AppTypography.bodyLarge)
                    .foregroundStyle(AppColors.onSurface)
                    .lineLimit(1)
                Text(verbatim: "\(album.itemsCount)")
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
            Spacer()
        }
        .contentShape(Rectangle())
    }

    @ViewBuilder
    private func cover(_ album: AlbumCard) -> some View {
        let size: CGFloat = 44
        if let url = album.coverPreviewURL {
            RemoteImage(url: url, contentMode: .fill) {
                AppColors.onSurface.opacity(0.08)
            }
            .frame(width: size, height: size)
            .clipShape(RoundedRectangle(cornerRadius: 8))
        } else {
            RoundedRectangle(cornerRadius: 8)
                .fill(AppColors.onSurface.opacity(0.08))
                .frame(width: size, height: size)
                .overlay {
                    Image(systemName: "rectangle.stack")
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
        }
    }

    private func load() async {
        isLoading = true
        do {
            let page = try await albums.listAlbums(limit: 100)
            items = page.albums
        } catch {
            errorText = domainErrorMessage(error)
        }
        isLoading = false
    }
}
