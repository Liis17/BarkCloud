import SwiftUI
import UIKit

/// Экран «Общий доступ» с двумя табами: «Мои публичные» и «Мне доступны».
/// Открывается из toolbar `FilesRootScreen` → `NavigationLink`. На этапе 1
/// «Мне доступны» — заглушка; будет реализована в этапе 3.
struct SharedHubScreen: View {
    @Environment(AppEnvironment.self) private var env
    @State private var tab: SharedHubTab = .myPublic
    @State private var mySharesVM: MySharesViewModel?
    @State private var sharedWithMeVM: SharedWithMeViewModel?

    var body: some View {
        VStack(spacing: 0) {
            Picker("", selection: $tab) {
                Text(String(localized: "shared_tab_public")).tag(SharedHubTab.myPublic)
                Text(String(localized: "shared_tab_with_me")).tag(SharedHubTab.sharedWithMe)
            }
            .pickerStyle(.segmented)
            .padding(.horizontal, 16)
            .padding(.top, 8)
            .padding(.bottom, 12)

            switch tab {
            case .myPublic:
                if let mySharesVM {
                    MySharesListView(vm: mySharesVM)
                } else {
                    ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            case .sharedWithMe:
                if let sharedWithMeVM {
                    SharedWithMeListView(vm: sharedWithMeVM)
                } else {
                    ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
        }
        .navigationTitle(String(localized: "shared_hub_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if mySharesVM == nil {
                mySharesVM = MySharesViewModel(cloud: env.cloudRepository)
            }
            await mySharesVM?.loadIfNeeded()
        }
        .task(id: tab) {
            // Лениво создаём и грузим «Мне доступны» только при первом переключении
            // на этот таб — не тратим запрос если пользователь не зайдёт.
            if tab == .sharedWithMe {
                if sharedWithMeVM == nil {
                    sharedWithMeVM = SharedWithMeViewModel(
                        cloud: env.cloudRepository,
                        users: env.userRepository
                    )
                }
                await sharedWithMeVM?.loadIfNeeded()
            }
        }
    }
}

enum SharedHubTab: Hashable {
    case myPublic
    case sharedWithMe
}
