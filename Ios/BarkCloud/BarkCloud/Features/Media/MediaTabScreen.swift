import SwiftUI

enum MediaSegment: Hashable {
    case all, albums
}

/// Контейнер вкладки Фото/Видео: сегмент «Всё / Альбомы» поверх общей сетки
/// медиа (`MediaGridScreen`) и сетки альбомов (`AlbumsGridScreen`).
struct MediaTabScreen: View {
    let kind: MediaKind

    @State private var segment: MediaSegment = .all

    var body: some View {
        VStack(spacing: 0) {
            Picker("", selection: $segment) {
                Text("media_segment_all").tag(MediaSegment.all)
                Text("media_segment_albums").tag(MediaSegment.albums)
            }
            .pickerStyle(.segmented)
            .padding(.horizontal, 16)
            .padding(.vertical, 8)

            switch segment {
            case .all:
                MediaGridScreen(kind: kind)
            case .albums:
                AlbumsGridScreen(kind: kind)
            }
        }
        .navigationTitle(String(localized: kind.titleKey))
        .navigationBarTitleDisplayMode(.inline)
    }
}
