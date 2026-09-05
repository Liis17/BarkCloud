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

    /// URL файла видео-ассета для QuickLook (свайп-просмотрщик). Для локальных
    /// видео `requestAVAsset` отдаёт `AVURLAsset` с прямым путём в контейнере
    /// медиатеки — копировать оригинал на диск не нужно (тяжёлые файлы не гоняем).
    /// `nil`, если ассет не является `AVURLAsset` (напр. slow-mo/композиция) —
    /// тогда просмотрщик покажет заглушку.
    func videoFileURL(for asset: PHAsset) async -> URL? {
        await withCheckedContinuation { cont in
            let options = PHVideoRequestOptions()
            options.isNetworkAccessAllowed = true
            options.deliveryMode = .automatic
            manager.requestAVAsset(forVideo: asset, options: options) { avAsset, _, _ in
                cont.resume(returning: (avAsset as? AVURLAsset)?.url)
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
            TemporaryFileCleanup.removeFileAndEmptyParent(
                at: fileURL,
                within: FileManager.default.temporaryDirectory
            )
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
