import Foundation
import UniformTypeIdentifiers

enum MimeIcon {
    static let folderSymbol = "folder"

    static func mime(forFileName name: String) -> String {
        let ext = (name as NSString).pathExtension
        guard !ext.isEmpty,
              let type = UTType(filenameExtension: ext),
              let mime = type.preferredMIMEType else {
            return "application/octet-stream"
        }
        return mime
    }

    static func iconSymbol(forFileName name: String) -> String {
        let mime = mime(forFileName: name)
        if mime.hasPrefix("image/") { return "photo" }
        if mime.hasPrefix("video/") { return "play.rectangle" }
        if mime.hasPrefix("audio/") { return "music.note" }
        if mime.contains("pdf") { return "doc.richtext" }
        if mime.contains("zip") || mime.contains("compressed") || mime.contains("archive") { return "archivebox" }
        if mime.hasPrefix("text/") { return "doc.text" }
        let ext = (name as NSString).pathExtension.lowercased()
        if ["swift", "kt", "java", "py", "js", "ts", "rb", "go", "rs", "cpp", "c", "h"].contains(ext) {
            return "chevron.left.forwardslash.chevron.right"
        }
        return "doc"
    }
}
