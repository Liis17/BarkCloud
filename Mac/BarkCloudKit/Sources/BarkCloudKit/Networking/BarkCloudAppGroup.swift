import Foundation

/// App Group, в котором `GrpcManager` (адрес сервера) и `SessionStore` (токены)
/// хранят данные, общие для главного приложения и расширений.
///
/// На **iOS** Apple не требует TeamID-префикс в App Group, поэтому id зашит
/// константой (`group.com.barkfluff.BarkCloud`) — совпадает с
/// `UploadConstants.appGroupID` и entitlements main app + Share Extension.
///
/// На **macOS** Apple **требует** TeamID-префикс
/// (`group.<TeamID>.com.barkfluff.BarkCloud`). TeamID разный у каждого
/// разработчика, поэтому в коде его захардкодить нельзя. Решение: build
/// setting `INFOPLIST_KEY_BarkCloudAppGroupID` у каждого macOS-таргета
/// подставляет `group.$(TeamIdentifierPrefix)com.barkfluff.BarkCloud`
/// в собранный Info.plist — мы читаем готовое значение через
/// `Bundle.main.object(forInfoDictionaryKey:)`. Entitlements тех же
/// таргетов содержат соответствующий `group.$(TeamIdentifierPrefix)...`.
public enum BarkCloudAppGroup {
    public static var identifier: String {
        #if os(iOS)
        return "group.com.barkfluff.BarkCloud"
        #else
        if let id = Bundle.main.object(forInfoDictionaryKey: "BarkCloudAppGroupID") as? String,
           id.hasPrefix("group.") {
            return id
        }
        // Fallback (билд без подставленного ключа). UserDefaults(suiteName:)
        // на macOS вернёт nil → код упадёт на `?? .standard`; конфиг
        // окажется в локальном sandbox и не будет shared с расширением.
        return "group.com.barkfluff.BarkCloud"
        #endif
    }

    /// Корень App Group container — общий каталог для shared-файлов
    /// (нашему расширению нужен для persistent cache, app может удалить файлы
    /// на logout). На iOS / при отсутствии App Group возвращает nil.
    public static var containerURL: URL? {
        FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: identifier)
    }
}
