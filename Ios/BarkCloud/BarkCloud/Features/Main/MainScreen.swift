import SwiftUI

struct MainScreen: View {
    @State private var selection: MainDestination = .default

    var body: some View {
        TabView(selection: $selection) {
            PlaceholderScreen(destination: .photos)
                .tabItem {
                    Label {
                        Text(MainDestination.photos.labelKey)
                    } icon: {
                        Image(systemName: selection == .photos ? MainDestination.photos.iconFilled : MainDestination.photos.iconOutlined)
                    }
                }
                .tag(MainDestination.photos)

            PlaceholderScreen(destination: .videos)
                .tabItem {
                    Label {
                        Text(MainDestination.videos.labelKey)
                    } icon: {
                        Image(systemName: selection == .videos ? MainDestination.videos.iconFilled : MainDestination.videos.iconOutlined)
                    }
                }
                .tag(MainDestination.videos)

            NavigationStack {
                FilesRootScreen()
            }
            .tabItem {
                Label {
                    Text(MainDestination.files.labelKey)
                } icon: {
                    Image(systemName: selection == .files ? MainDestination.files.iconFilled : MainDestination.files.iconOutlined)
                }
            }
            .tag(MainDestination.files)

            PlaceholderScreen(destination: .shared)
                .tabItem {
                    Label {
                        Text(MainDestination.shared.labelKey)
                    } icon: {
                        Image(systemName: selection == .shared ? MainDestination.shared.iconFilled : MainDestination.shared.iconOutlined)
                    }
                }
                .tag(MainDestination.shared)

            PlaceholderScreen(destination: .settings)
                .tabItem {
                    Label {
                        Text(MainDestination.settings.labelKey)
                    } icon: {
                        Image(systemName: selection == .settings ? MainDestination.settings.iconFilled : MainDestination.settings.iconOutlined)
                    }
                }
                .tag(MainDestination.settings)
        }
    }
}
