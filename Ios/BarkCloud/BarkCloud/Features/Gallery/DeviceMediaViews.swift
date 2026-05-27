import SwiftUI
import UIKit
import Photos
import AVKit

/// Загрузка превью/оригиналов ассетов устройства через PhotoKit. Один общий
/// кэширующий менеджер. Методы оборачивают callback-API в async; режим
/// `.highQualityFormat` гарантирует единственный вызов обработчика (безопасно
/// для `withCheckedContinuation`).
final class DeviceMediaImageLoader: @unchecked Sendable {
    static let shared = DeviceMediaImageLoader()
    private let manager = PHCachingImageManager()

    func thumbnail(for asset: PHAsset, targetSize: CGSize) async -> UIImage? {
        await withCheckedContinuation { cont in
            let options = PHImageRequestOptions()
            options.deliveryMode = .highQualityFormat
            options.resizeMode = .fast
            options.isNetworkAccessAllowed = true
            manager.requestImage(for: asset, targetSize: targetSize, contentMode: .aspectFill, options: options) { image, _ in
                cont.resume(returning: image)
            }
        }
    }

    func fullImage(for asset: PHAsset) async -> UIImage? {
        await withCheckedContinuation { cont in
            let options = PHImageRequestOptions()
            options.deliveryMode = .highQualityFormat
            options.isNetworkAccessAllowed = true
            manager.requestImage(
                for: asset,
                targetSize: PHImageManagerMaximumSize,
                contentMode: .aspectFit,
                options: options
            ) { image, _ in
                cont.resume(returning: image)
            }
        }
    }

    func playerItem(for asset: PHAsset) async -> AVPlayerItem? {
        await withCheckedContinuation { cont in
            let options = PHVideoRequestOptions()
            options.isNetworkAccessAllowed = true
            options.deliveryMode = .automatic
            manager.requestPlayerItem(forVideo: asset, options: options) { item, _ in
                cont.resume(returning: item)
            }
        }
    }
}

/// Квадратная ячейка медиатеки устройства с бейджем видео и галкой выбора.
struct DeviceMediaThumb: View {
    let asset: PHAsset
    let isSelecting: Bool
    let isSelected: Bool
    /// Файл уже есть в облаке (определяется по SHA256-хешу) — рисуем иконку облака.
    var isInCloud: Bool = false

    @State private var image: UIImage?

    var body: some View {
        // `SquareThumbClip` даёт картинке строго квадратный фрейм: при `scaledToFill`
        // она иначе переполняет квадрат и её невидимый «хвост» перехватывает тапы по
        // соседней строке сетки (визуальная обрезка хит-тест не ограничивает).
        SquareThumbClip(cornerRadius: 4) {
            if let image {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                AppColors.onSurface.opacity(0.08)
            }
        }
        .overlay(alignment: .bottomTrailing) {
            if asset.mediaType == .video {
                Image(systemName: "play.circle.fill")
                    .font(.system(size: 18))
                    .foregroundStyle(.white)
                    .shadow(radius: 2)
                    .padding(6)
            }
        }
        .overlay(alignment: .topLeading) {
            if isInCloud {
                Image(systemName: "checkmark.icloud.fill")
                    .font(.system(size: 16))
                    .foregroundStyle(.white)
                    .shadow(radius: 2)
                    .padding(6)
            }
        }
        .overlay(alignment: .topTrailing) {
            if isSelecting {
                Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 20))
                    .foregroundStyle(isSelected ? AppColors.accent : .white)
                    .shadow(radius: 2)
                    .padding(6)
            }
        }
        .overlay {
            if isSelecting && isSelected {
                RoundedRectangle(cornerRadius: 4)
                    .fill(AppColors.accent.opacity(0.25))
            }
        }
        .task(id: asset.localIdentifier) {
            let side = 130 * UIScreen.main.scale
            image = await DeviceMediaImageLoader.shared.thumbnail(
                for: asset,
                targetSize: CGSize(width: side, height: side)
            )
        }
    }
}

/// Полноэкранный просмотр ассета устройства: фото — изображение, видео — плеер.
struct DeviceMediaViewer: View {
    let asset: PHAsset

    @State private var image: UIImage?
    @State private var player: AVPlayer?
    @State private var failed = false

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if asset.mediaType == .video {
                if let player {
                    VideoPlayer(player: player).ignoresSafeArea()
                } else {
                    statusView
                }
            } else {
                if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFit()
                } else {
                    statusView
                }
            }
        }
        .task { await load() }
    }

    @ViewBuilder
    private var statusView: some View {
        if failed {
            ContentUnavailableView(String(localized: "preview_failed"), systemImage: "exclamationmark.triangle")
                .foregroundStyle(.white)
        } else {
            ProgressView().tint(.white)
        }
    }

    private func load() async {
        if asset.mediaType == .video {
            if let item = await DeviceMediaImageLoader.shared.playerItem(for: asset) {
                let player = AVPlayer(playerItem: item)
                self.player = player
                player.play()
            } else {
                failed = true
            }
        } else {
            image = await DeviceMediaImageLoader.shared.fullImage(for: asset)
            if image == nil { failed = true }
        }
    }
}
