import UIKit
import QuickLookThumbnailing

enum ThumbnailLoader {
    private static let cache: NSCache<NSString, UIImage> = {
        let c = NSCache<NSString, UIImage>()
        c.countLimit = 200
        return c
    }()

    static func thumbnail(for path: String, lastModified: Date, size: CGSize = CGSize(width: 96, height: 96)) async -> UIImage? {
        let key = "\(path)|\(Int(lastModified.timeIntervalSince1970))" as NSString
        if let cached = cache.object(forKey: key) { return cached }

        let scale = await MainActor.run { UIScreen.main.scale }
        let request = QLThumbnailGenerator.Request(
            fileAt: URL(fileURLWithPath: path),
            size: size,
            scale: scale,
            representationTypes: .thumbnail
        )
        do {
            let rep = try await QLThumbnailGenerator.shared.generateBestRepresentation(for: request)
            cache.setObject(rep.uiImage, forKey: key)
            return rep.uiImage
        } catch {
            return nil
        }
    }

    static func canRender(forFileName name: String) -> Bool {
        let mime = MimeIcon.mime(forFileName: name)
        return mime.hasPrefix("image/") || mime.hasPrefix("video/") || mime.contains("pdf")
    }
}
