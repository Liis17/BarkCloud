import Foundation
import UIKit
import WidgetKit
import BarkCloudKit

/// Одна запись манифеста недавних медиа для виджета. Кодируется идентичной
/// структурой и в виджете (`RecentMediaWidget`) — это контракт между таргетами.
struct RecentMediaEntry: Codable {
    let id: String
    let fileName: String
    let isVideo: Bool
    /// Имя файла миниатюры внутри каталога `recent_widget` в App Group.
    let file: String
}

/// Канал передачи недавних облачных медиа в `RecentMediaWidget`. Виджет не может
/// синхронно тянуть превью по сети, поэтому main app скачивает и даунскейлит
/// миниатюры последних N фото в App Group контейнер (`recent_widget/*.jpg`) и пишет
/// манифест в App Group `UserDefaults`. Виджет читает манифест и грузит JPEG с диска.
enum RecentMediaWidgetBridge {
    private static let manifestKey = "recent_widget.manifest"
    private static let maxItems = 8
    private static let thumbMaxDimension: CGFloat = 300

    private static var defaults: UserDefaults? {
        UserDefaults(suiteName: UploadConstants.appGroupID)
    }

    /// Каталог миниатюр в App Group (создаётся при первом обращении).
    private static var directory: URL? {
        guard let base = UploadConstants.appGroupURL else { return nil }
        let dir = base.appendingPathComponent("recent_widget", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// Обновить кэш недавних медиа. Берём первые `maxItems`, тяжёлую работу
    /// (сеть + перерисовка) уносим в фоновую задачу, чтобы не блокировать reload.
    static func update(items: [MediaItem]) {
        let picked = items.prefix(maxItems).map {
            (id: $0.id, fileName: $0.fileName, isVideo: $0.isVideo, url: $0.thumbnailURL)
        }
        Task.detached(priority: .utility) {
            await rebuild(picked)
        }
    }

    private static func rebuild(_ items: [(id: String, fileName: String, isVideo: Bool, url: URL?)]) async {
        guard let directory, let defaults else { return }
        let fm = FileManager.default
        var entries: [RecentMediaEntry] = []
        for item in items {
            guard let url = item.url else { continue }
            let file = "\(item.id).jpg"
            let dest = directory.appendingPathComponent(file)
            if !fm.fileExists(atPath: dest.path) {
                guard let (data, _) = try? await InsecureHTTP.session.data(from: url),
                      let image = UIImage(data: data),
                      let jpeg = downscaledJPEG(image) else { continue }
                try? jpeg.write(to: dest)
            }
            entries.append(RecentMediaEntry(id: item.id, fileName: item.fileName, isVideo: item.isVideo, file: file))
        }
        // Прунинг: убрать миниатюры, которых больше нет в манифесте.
        let keep = Set(entries.map(\.file))
        if let existing = try? fm.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
            for f in existing where !keep.contains(f.lastPathComponent) { try? fm.removeItem(at: f) }
        }
        if let data = try? JSONEncoder().encode(entries) {
            defaults.set(data, forKey: manifestKey)
        }
        WidgetCenter.shared.reloadTimelines(ofKind: "RecentMediaWidget")
    }

    private static func downscaledJPEG(_ image: UIImage) -> Data? {
        let scale = min(1, thumbMaxDimension / max(image.size.width, image.size.height))
        let target = CGSize(width: image.size.width * scale, height: image.size.height * scale)
        let renderer = UIGraphicsImageRenderer(size: target)
        let resized = renderer.image { _ in image.draw(in: CGRect(origin: .zero, size: target)) }
        return resized.jpegData(compressionQuality: 0.8)
    }

    /// Полная очистка кэша недавних — при сбросе локального состояния (выход/смена
    /// аккаунта), чтобы превью прежнего аккаунта не оставались на устройстве.
    static func clear() {
        defaults?.removeObject(forKey: manifestKey)
        if let directory { try? FileManager.default.removeItem(at: directory) }
    }
}
