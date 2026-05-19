import SwiftUI

@main
struct BarkCloudApp: App {
    @State private var env = AppEnvironment()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(env)
                .modifier(BarkCloudTheme())
        }
    }
}
