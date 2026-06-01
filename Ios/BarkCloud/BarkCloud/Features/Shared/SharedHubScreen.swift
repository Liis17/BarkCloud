import SwiftUI
import UIKit

/// Экран «Общий доступ» с двумя табами: «Мои публичные» и «Мне доступны».
/// Открывается из toolbar `FilesRootScreen` → `NavigationLink`. На этапе 1
/// «Мне доступны» — заглушка; будет реализована в этапе 3.
struct SharedHubScreen: View {
    @Environment(AppEnvironment.self) private var env
    @State private var tab: SharedHubTab = .myPublic
    @State private var mySharesVM: MySharesViewModel?

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
                comingSoonView
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
    }

    private var comingSoonView: some View {
        VStack(spacing: 16) {
            Image(systemName: "tray.and.arrow.down")
                .font(.system(size: 56))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text(String(localized: "shared_with_me_empty_title"))
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurface)
                .multilineTextAlignment(.center)
            Text(String(localized: "shared_with_me_coming_soon"))
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

enum SharedHubTab: Hashable {
    case myPublic
    case sharedWithMe
}
