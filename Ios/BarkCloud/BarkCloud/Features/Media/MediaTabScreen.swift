import SwiftUI

/// Сегмент облачного медиа-экрана.
enum CloudMediaSegment: Hashable {
    case photos, videos, albums
}

/// Вкладка «Альбомы»: облачные медиа с переключателем «Фото / Видео / Альбомы».
/// Фото и видео — сетка `MediaGridScreen` по типу; «Альбомы» — сетка `AlbumsGridScreen`
/// без фильтра по типу (альбом показывает и фото, и видео).
struct CloudMediaScreen: View {
    @State private var segment: CloudMediaSegment = .photos

    var body: some View {
        VStack(spacing: 0) {
            Picker("", selection: $segment) {
                Text("media_segment_photos").tag(CloudMediaSegment.photos)
                Text("media_segment_videos").tag(CloudMediaSegment.videos)
                Text("media_segment_albums").tag(CloudMediaSegment.albums)
            }
            .pickerStyle(.segmented)
            .padding(.horizontal, 16)
            .padding(.vertical, 8)

            switch segment {
            case .photos:
                MediaGridScreen(kind: .photo)
            case .videos:
                MediaGridScreen(kind: .video)
            case .albums:
                AlbumsGridScreen(kind: nil)
            }
        }
        .navigationTitle(String(localized: "tab_albums"))
        .navigationBarTitleDisplayMode(.inline)
    }
}
