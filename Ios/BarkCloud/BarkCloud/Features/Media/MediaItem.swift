import Foundation

/// Элемент медиа-сетки. Future-ready под `UserImageItem` / `UploadFileInfo`
/// из `files_api.proto` (поле `file.preview` → `thumbnailURL`).
struct MediaItem: Identifiable, Hashable {
    let id: String
    /// URL превью. `nil` у плейсхолдеров — ячейка рисует скелетон.
    let thumbnailURL: URL?
    let isVideo: Bool

    /// Плейсхолдеры для скелетон-режима, пока получение с сервера не реализовано.
    static func placeholders(count: Int, isVideo: Bool) -> [MediaItem] {
        (0..<count).map { i in
            MediaItem(id: "placeholder-\(i)", thumbnailURL: nil, isVideo: isVideo)
        }
    }
}
