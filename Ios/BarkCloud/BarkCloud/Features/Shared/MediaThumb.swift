import SwiftUI

/// Квадратная ячейка-превью медиа (фото/видео) с бейджем видео.
/// Используется сетками галереи и альбомов.
struct MediaThumb: View {
    let thumbnailURL: URL?
    let isVideo: Bool

    var body: some View {
        RoundedRectangle(cornerRadius: 4)
            .fill(AppColors.onSurface.opacity(0.08))
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                if let thumbnailURL {
                    RemoteImage(url: thumbnailURL, contentMode: .fill) { Color.clear }
                        .clipShape(RoundedRectangle(cornerRadius: 4))
                }
            }
            .overlay(alignment: .bottomTrailing) {
                if isVideo && thumbnailURL != nil {
                    Image(systemName: "play.circle.fill")
                        .font(.system(size: 18))
                        .foregroundStyle(.white)
                        .shadow(radius: 2)
                        .padding(6)
                }
            }
            .clipped()
    }
}
