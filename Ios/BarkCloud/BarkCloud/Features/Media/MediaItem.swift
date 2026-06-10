import Foundation
import BarkCloudKit

/// Элемент медиа-сетки (фото/видео). Заполняется из `MediaAsset` (`UploadFileInfo`).
struct MediaItem: Identifiable, Hashable {
    let id: String              // file_id блоба
    /// URL превью. `nil` у плейсхолдеров — ячейка рисует скелетон.
    let thumbnailURL: URL?
    /// Фактическая ширина выбранного превью — ключ дискового кеша.
    let previewWidth: Int
    let isVideo: Bool
    let fileName: String
    let date: Date
    /// Полные метаданные файла для экрана свойств. `nil` у плейсхолдеров.
    let asset: MediaAsset?

    init(id: String, thumbnailURL: URL?, previewWidth: Int = 512, isVideo: Bool, fileName: String = "") {
        self.id = id
        self.thumbnailURL = thumbnailURL
        self.previewWidth = previewWidth
        self.isVideo = isVideo
        self.fileName = fileName
        self.date = Date(timeIntervalSince1970: 0)
        self.asset = nil
    }

    init(asset: MediaAsset) {
        let preview = asset.preview(preferredWidth: 512)
        self.id = asset.id
        self.thumbnailURL = preview?.url
        self.previewWidth = preview?.width ?? 512
        self.isVideo = asset.isVideo
        self.fileName = asset.fileName
        self.date = asset.uploadedAt ?? asset.createdAt
        self.asset = asset
    }

    /// Плейсхолдеры для скелетон-режима, пока идёт первая загрузка с сервера.
    static func placeholders(count: Int, isVideo: Bool) -> [MediaItem] {
        (0..<count).map { i in
            MediaItem(id: "placeholder-\(i)", thumbnailURL: nil, isVideo: isVideo)
        }
    }
}
