import SwiftUI

struct MainScreen: View {
    let onSignOut: () -> Void
    @State private var selection: MainDestination = .default

    var body: some View {
        TabView(selection: $selection) {
            NavigationStack {
                GalleryScreen()
            }
            .tabItem { tabLabel(.gallery) }
            .tag(MainDestination.gallery)

            NavigationStack {
                FilesRootScreen()
            }
            .tabItem { tabLabel(.files) }
            .tag(MainDestination.files)

            NavigationStack {
                CloudMediaScreen()
            }
            .tabItem { tabLabel(.albums) }
            .tag(MainDestination.albums)

            NavigationStack {
                TrashScreen()
            }
            .tabItem { tabLabel(.trash) }
            .tag(MainDestination.trash)

            SettingsScreen(onSignOut: onSignOut)
                .tabItem { tabLabel(.settings) }
                .tag(MainDestination.settings)
        }
    }

    private func tabLabel(_ destination: MainDestination) -> some View {
        Label {
            Text(destination.labelKey)
        } icon: {
            Image(systemName: selection == destination ? destination.iconFilled : destination.iconOutlined)
        }
    }
}
