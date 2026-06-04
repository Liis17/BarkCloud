import Foundation

/// App Group, в котором `GrpcManager` (адрес сервера) и `SessionStore` (токены)
/// хранят данные, общие для главного приложения и расширений.
///
/// На iOS — тот же id, что у `UploadConstants.appGroupID` и в entitlements main app
/// и Share Extension (`group.com.barkfluff.BarkCloud`). На macOS контейнер-app и
/// FSKit-расширение делят свой App Group — id согласуется с их entitlements на
/// Этапе 1 (см. PLAN.md 1.5/1.6); пока совпадает с iOS-значением как заглушка.
enum BarkCloudAppGroup {
    #if os(iOS)
    static let identifier = "group.com.barkfluff.BarkCloud"
    #else
    static let identifier = "group.com.barkfluff.BarkCloud"
    #endif
}
