import Foundation
import SwiftUI

/// Вид медиа-вкладки. Определяет заголовок, иконку и текст пустого состояния
/// для переиспользуемого `MediaGridScreen`.
enum MediaKind: Hashable {
    case photo
    case video

    /// Заголовок навбара (совпадает с подписью таба).
    var titleKey: LocalizedStringResource {
        switch self {
        case .photo: return "tab_photos"
        case .video: return "tab_videos"
        }
    }

    /// Текст пустого состояния — показывается, когда сервер вернул 0 элементов
    /// (на будущее; сейчас экран всегда в скелетон-режиме).
    var emptyKey: LocalizedStringResource {
        switch self {
        case .photo: return "placeholder_photos"
        case .video: return "placeholder_videos"
        }
    }

    var emptyIcon: String {
        switch self {
        case .photo: return "photo.on.rectangle"
        case .video: return "play.rectangle"
        }
    }

    var isVideo: Bool { self == .video }
}
