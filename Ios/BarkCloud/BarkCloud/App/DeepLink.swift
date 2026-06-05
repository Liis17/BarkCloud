import Foundation

/// Глубокие ссылки `barkcloud://<host>` для тапов по виджетам. Парсятся в
/// `RootView.onOpenURL` и складываются в `AppEnvironment.pendingDeepLink`,
/// откуда `MainScreen` переключает таб (и при необходимости пушит экран).
enum DeepLink: Equatable {
    case albums           // облачные медиа
    case trash            // корзина
    case vault            // сейф (через таб «Настройки»)
    case media(id: String) // конкретное облачное фото (вкладка «Альбомы» → пейджер)

    init?(url: URL) {
        guard url.scheme == "barkcloud" else { return nil }
        switch url.host {
        case "albums": self = .albums
        case "trash": self = .trash
        case "vault": self = .vault
        case "media":
            let id = url.path.dropFirst() // убрать ведущий "/"
            guard !id.isEmpty else { return nil }
            self = .media(id: String(id))
        default: return nil
        }
    }

    /// Таб, который нужно сделать активным для этой ссылки.
    var tab: MainDestination {
        switch self {
        case .albums, .media: return .albums
        case .trash: return .trash
        case .vault: return .settings
        }
    }
}
