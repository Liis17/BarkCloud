import SwiftUI

struct RootView: View {
    @Environment(AppEnvironment.self) private var env
    @State private var isAuthenticated = false

    var body: some View {
        Group {
            if isAuthenticated || env.sessionStore.hasValidRefreshToken() {
                MainScreen(onSignOut: { isAuthenticated = false })
            } else {
                LoginScreen(onAuthenticated: { isAuthenticated = true })
            }
        }
        .animation(.default, value: isAuthenticated)
    }
}
