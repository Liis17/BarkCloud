import SwiftUI

struct MainScreen: View {
    let onSignOut: () -> Void
    @Environment(AppEnvironment.self) private var env
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
        .overlay(alignment: .bottom) { uploadBannerOverlay }
        .onAppear { applyPendingDeepLink() }
        .onChange(of: env.pendingDeepLink) { _, _ in applyPendingDeepLink() }
        .onChange(of: selection) { _, new in
            // При возврате на вкладку «Галерея»: re-scan медиатеки в BackupManager
            // (новые ассеты в pendingUpload), reload в GalleryViewModel (пересобрать
            // assets через PhotoKit — PHPhotoLibraryChangeObserver мог пропустить
            // изменения если процесс был suspended). Сигнал галерее идёт через
            // NotificationCenter, чтобы не таскать ref на vm из родительского экрана.
            if new == .gallery {
                Task { await env.backupManager.refreshScanForNewAssets() }
                NotificationCenter.default.post(name: .galleryDidFocus, object: nil)
            }
        }
    }

    /// Плавающая плашка прогресса. Видна на любой вкладке пока есть активная
    /// загрузка (см. [[UploadProgressObserver]]). По тапу — открыть BackupSheet,
    /// если идёт автозагрузка медиатеки (там детальный прогресс); иначе ничего
    /// не делать — баннер просто как индикатор.
    @ViewBuilder
    private var uploadBannerOverlay: some View {
        if env.uploadProgress.isActive {
            GlobalUploadBanner(observer: env.uploadProgress) {
                if env.uploadProgress.currentSource == .backup {
                    selection = .gallery
                }
            }
            .padding(.horizontal, 12)
            .padding(.bottom, 56)  // приподнять над TabBar
            .animation(.spring(response: 0.4, dampingFraction: 0.85), value: env.uploadProgress.isActive)
            .transition(.move(edge: .bottom).combined(with: .opacity))
        }
    }

    /// Обработать тап по виджету (`barkcloud://…`): переключить таб, для сейфа —
    /// попросить `SettingsScreen` запушить `VaultScreen`. Ссылку обнуляем, чтобы
    /// повторный заход не срабатывал ещё раз.
    private func applyPendingDeepLink() {
        guard let link = env.pendingDeepLink else { return }
        selection = link.tab
        if link == .vault { env.presentVault = true }
        env.pendingDeepLink = nil
    }

    private func tabLabel(_ destination: MainDestination) -> some View {
        Label {
            Text(destination.labelKey)
        } icon: {
            Image(systemName: selection == destination ? destination.iconFilled : destination.iconOutlined)
        }
    }
}
