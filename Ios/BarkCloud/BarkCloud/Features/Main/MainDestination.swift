import Foundation
import SwiftUI

enum MainDestination: Hashable, CaseIterable {
    case photos
    case videos
    case files
    case shared
    case settings

    static let `default`: MainDestination = .files

    var labelKey: LocalizedStringResource {
        switch self {
        case .photos: return "tab_photos"
        case .videos: return "tab_videos"
        case .files: return "tab_files"
        case .shared: return "tab_shared"
        case .settings: return "tab_settings"
        }
    }

    var placeholderKey: LocalizedStringResource {
        switch self {
        case .photos: return "placeholder_photos"
        case .videos: return "placeholder_videos"
        case .files: return "placeholder_files"
        case .shared: return "placeholder_shared"
        case .settings: return "placeholder_settings"
        }
    }

    var iconOutlined: String {
        switch self {
        case .photos: return "photo.on.rectangle"
        case .videos: return "play.rectangle"
        case .files: return "folder"
        case .shared: return "person.2"
        case .settings: return "gearshape"
        }
    }

    var iconFilled: String {
        switch self {
        case .photos: return "photo.on.rectangle.fill"
        case .videos: return "play.rectangle.fill"
        case .files: return "folder.fill"
        case .shared: return "person.2.fill"
        case .settings: return "gearshape.fill"
        }
    }
}
