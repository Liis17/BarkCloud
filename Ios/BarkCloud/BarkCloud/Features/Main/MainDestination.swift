import Foundation
import SwiftUI

/// Пять вкладок нижней навигации. Порядок = порядок в таб-баре.
/// По умолчанию открывается «Альбомы».
enum MainDestination: Hashable, CaseIterable {
    case gallery   // локальная медиатека устройства (PhotoKit)
    case files     // файлы: устройство + облако + общие
    case albums    // облачные медиа: Фото / Видео / Альбомы
    case trash     // корзина облака
    case settings  // профиль и настройки

    static let `default`: MainDestination = .albums

    var labelKey: LocalizedStringResource {
        switch self {
        case .gallery: return "tab_gallery"
        case .files: return "tab_files"
        case .albums: return "tab_albums"
        case .trash: return "tab_trash"
        case .settings: return "tab_settings"
        }
    }

    var iconOutlined: String {
        switch self {
        case .gallery: return "photo.on.rectangle"
        case .files: return "folder"
        case .albums: return "rectangle.stack"
        case .trash: return "trash"
        case .settings: return "gearshape"
        }
    }

    var iconFilled: String {
        switch self {
        case .gallery: return "photo.on.rectangle.fill"
        case .files: return "folder.fill"
        case .albums: return "rectangle.stack.fill"
        case .trash: return "trash.fill"
        case .settings: return "gearshape.fill"
        }
    }
}
