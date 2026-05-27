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

    /// Экспортирует оригинал фото-ассета во временный файл и возвращает его URL.
    /// Нужен для QuickLook-просмотра (`FilePreviewController`), который даёт
    /// нативные фишки iOS: выделение объекта на фото, Live Text, зум, шаринг.
    /// Поток пишется на диск чанками (без удержания всего файла в памяти);
    /// приоритет ресурсов тот же, что при загрузке в облако — имя файла
    /// сохраняет расширение, чтобы QuickLook определил тип.
    func exportPhotoToTempFile(for asset: PHAsset) async -> URL? {
        let resources = PHAssetResource.assetResources(for: asset)
        let preferred: [PHAssetResourceType] = [.photo, .fullSizePhoto]
        let resource = preferred.compactMap { type in resources.first { $0.type == type } }.first
            ?? resources.first
        guard let resource else { return nil }

        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let fileURL = dir.appendingPathComponent(resource.originalFilename)

        let options = PHAssetResourceRequestOptions()
        options.isNetworkAccessAllowed = true
        do {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                PHAssetResourceManager.default().writeData(for: resource, toFile: fileURL, options: options) { error in
                    if let error { cont.resume(throwing: error) } else { cont.resume() }
                }
            }
            return fileURL
        } catch {
            return nil
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

/// Полноэкранный просмотр ассета устройства: фото — через QuickLook
/// (`FilePreviewController`), что даёт нативные фишки iOS (выделение объекта,
/// Live Text, зум, шаринг) — как в просмотрщике Альбомов; видео — плеер.
struct DeviceMediaViewer: View {
    let asset: PHAsset

    @State private var photoURL: URL?
    @State private var player: AVPlayer?
    @State private var failed = false

    var body: some View {
        Group {
            if asset.mediaType == .video {
                ZStack {
                    Color.black.ignoresSafeArea()
                    if let player {
                        VideoPlayer(player: player).ignoresSafeArea()
                    } else {
                        statusView
                    }
                }
            } else {
                if let photoURL {
                    FilePreviewController(fileURL: photoURL)
                        .ignoresSafeArea()
                } else {
                    ZStack {
                        Color.black.ignoresSafeArea()
                        statusView
                    }
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
            photoURL = await DeviceMediaImageLoader.shared.exportPhotoToTempFile(for: asset)
            if photoURL == nil { failed = true }
        }
    }
}
