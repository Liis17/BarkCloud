import SwiftUI

struct FilesRootScreen: View {
    @Environment(AppEnvironment.self) private var env
    @State private var vm = FilesRootViewModel()

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                section(titleKey: "files_section_on_device") {
                    NavigationLink {
                        LocalBrowserScreen(
                            initialPath: StoragePermission.externalRoot.path,
                            rootLabel: String(localized: "files_storage_root")
                        )
                    } label: {
                        cardRow(
                            iconName: "internaldrive",
                            title: String(localized: "files_storage_root"),
                            subtitle: String(localized: "files_on_device_subtitle")
                        )
                    }
                    .buttonStyle(.plain)
                }

                section(titleKey: "files_section_server") {
                    serverFolders
                }

                section(titleKey: "files_section_shared") {
                    sharedFiles
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 24)
        }
        .navigationTitle(String(localized: "files_root_title"))
        .task { await vm.loadSummary(cloud: env.cloudRepository) }
    }

    @ViewBuilder
    private var serverFolders: some View {
        NavigationLink {
            CloudBrowserScreen(directoryID: "", title: String(localized: "files_cloud_storage"))
        } label: {
            cardRow(
                iconName: "cloud",
                title: String(localized: "files_cloud_storage"),
                subtitle: cloudSubtitle
            )
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private var sharedFiles: some View {
        NavigationLink {
            SharedHubScreen()
        } label: {
            cardRow(
                iconName: "person.2",
                title: String(localized: "files_shared_title"),
                subtitle: String(localized: "files_shared_subtitle")
            )
        }
        .buttonStyle(.plain)
    }

    private var cloudSubtitle: String {
        if vm.state.isLoading { return String(localized: "files_loading") }
        if vm.state.failed { return String(localized: "files_server_empty") }
        return FormatUtils.formatChildCount(vm.state.folderCount + vm.state.fileCount)
    }

    @ViewBuilder
    private func section<Content: View>(titleKey: LocalizedStringResource, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(titleKey)
                .font(AppTypography.titleSmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .textCase(.uppercase)
            content()
        }
    }

    @ViewBuilder
    private func cardRow(iconName: String, title: String, subtitle: String) -> some View {
        HStack(spacing: 16) {
            Image(systemName: iconName)
                .font(.system(size: 22))
                .frame(width: 40, height: 40)
                .foregroundStyle(AppColors.accent)
                .background(AppColors.accent.opacity(0.12))
                .clipShape(RoundedRectangle(cornerRadius: 10))
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(AppTypography.titleMedium)
                Text(subtitle).font(AppTypography.bodySmall).foregroundStyle(AppColors.onSurfaceVariant)
            }
            Spacer()
            Image(systemName: "chevron.right")
                .foregroundStyle(AppColors.onSurfaceVariant)
        }
        .padding(16)
        .background(AppColors.onSurface.opacity(0.04))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }
}
