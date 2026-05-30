import Foundation
import Observation

/// UI-обёртка над `ServerConfig` (App Group UserDefaults). Источник истины для
/// гейта первого запуска в `RootView` и для формы `ServerSetupScreen`.
@MainActor
@Observable
final class ServerConfigStore {
    private(set) var config: ServerConfig
    private(set) var isConfigured: Bool

    init() {
        self.config = ServerConfig.current
        self.isConfigured = ServerConfig.isConfigured
    }

    func save(_ newConfig: ServerConfig) {
        newConfig.persist()
        config = newConfig
        isConfigured = true
    }

    /// Сброс конфигурации при полном выходе/wipe — возвращает на экран ввода адресов.
    func reset() {
        ServerConfig.clear()
        config = .production
        isConfigured = false
    }
}
