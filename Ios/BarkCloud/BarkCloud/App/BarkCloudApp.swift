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
            // Возврат на передний план (в т.ч. после шеринга) — догрузить ящик.
            if phase == .active { env.shareInboxUploader.uploadPendingIfNeeded() }
            // 30-секундный grace для блокировки приложения.
            env.appLock.handleScenePhase(phase)
        }
    }
}
