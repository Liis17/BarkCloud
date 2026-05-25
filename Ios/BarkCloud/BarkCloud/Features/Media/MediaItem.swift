import Foundation

/// Элемент медиа-сетки (фото/видео). Заполняется из `MediaAsset` (`UploadFileInfo`).
struct MediaItem: Identifiable, Hashable {
    let id: String              // file_id блоба
    /// URL превью. `nil` у плейсхолдеров — ячейка рисует скелетон.
    let thumbnailURL: URL?
    let isVideo: Bool
    let fileName: String

    init(id: String, thumbnailURL: URL?, isVideo: Bool, fileName: String = "") {
        self.id = id
        self.thumbnailURL = thumbnailURL
        self.isVideo = isVideo
        self.fileName = fileName
    }

    init(asset: MediaAsset) {
        self.id = asset.id
        self.thumbnailURL = asset.previewURL(preferredWidth: 512)
        self.isVideo = asset.isVideo
        self.fileName = asset.fileName
    }

    /// Плейсхолдеры для скелетон-режима, пока идёт первая загрузка с сервера.
    static func placeholders(count: Int, isVideo: Bool) -> [MediaItem] {
        (0..<count).map { i in
            MediaItem(id: "placeholder-\(i)", thumbnailURL: nil, isVideo: isVideo)
        }
    }
}
