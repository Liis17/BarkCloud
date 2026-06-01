import SwiftUI

@main
struct BarkCloudApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var env = AppEnvironment()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(env)
                .modifier(BarkCloudTheme())
        }
        .onChange(of: scenePhase) { _, phase in
            // Возврат на передний план (в т.ч. после шеринга): догрузить ящик,
            // пересканировать медиатеку на новые фото, вернуть Live Activity
            // в обычный режим (с прогрессом).
            if phase == .active {
                env.shareInboxUploader.uploadPendingIfNeeded()
                Task {
                    await env.backupManager.refreshScanForNewAssets()
                    await UploadLiveActivityController.shared.setForegroundActive(true)
                }
            }
            // В фоне background URLSession часто стопорится → Live Activity
            // переключается на «Откройте BarkCloud, чтобы продолжить».
            if phase == .background {
                Task { await UploadLiveActivityController.shared.setForegroundActive(false) }
            }
            // 30-секундный grace для блокировки приложения.
            env.appLock.handleScenePhase(phase)
        }
    }
}
