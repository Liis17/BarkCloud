import SwiftUI

/// Обрезает «заполняющее» (`contentMode: .fill` / `scaledToFill`) содержимое строго
/// по квадрату ячейки — и визуально, и по области нажатий.
///
/// Без этого фрейм fill-картинки переполняет квадрат по большей стороне (заполнение),
/// `.clipped()`/`.clipShape` прячут переполнение лишь визуально, но НЕ обрезают
/// хит-тест — и невидимый «хвост» картинки соседней (нижней) строки перехватывает
/// тап по текущей ячейке. Здесь содержимое получает явный квадратный фрейм через
/// `GeometryReader`, поэтому его область нажатий совпадает с ячейкой.
struct SquareThumbClip<Content: View>: View {
    var cornerRadius: CGFloat = 4
    @ViewBuilder var content: () -> Content

    var body: some View {
        Color.clear
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                GeometryReader { geo in
                    content()
                        .frame(width: geo.size.width, height: geo.size.height)
                        .clipped()
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius))
            .contentShape(Rectangle())
    }
}

/// Квадратная ячейка-превью медиа (фото/видео) с бейджем видео.
/// Используется сетками галереи и альбомов.
struct MediaThumb: View {
    let thumbnailURL: URL?
    let isVideo: Bool

    var body: some View {
        SquareThumbClip(cornerRadius: 4) {
            if let thumbnailURL {
                RemoteImage(url: thumbnailURL, contentMode: .fill) {
                    AppColors.onSurface.opacity(0.08)
                }
            } else {
                AppColors.onSurface.opacity(0.08)
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
    }
}
