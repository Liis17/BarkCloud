import SwiftUI
import PhotosUI

/// Таб «Настройки»: профиль (аватар/имя/bio), переходы к редактированию,
/// приватности и устройствам, информация о хранилище, выход и удаление аккаунта.
struct SettingsScreen: View {
    @Environment(AppEnvironment.self) private var env
    let onSignOut: () -> Void

    @State private var vm: ProfileViewModel?
    @State private var avatarItem: PhotosPickerItem?
    @State private var showDeleteConfirm = false
    @State private var showSignOutConfirm = false
    @State private var isProcessing = false

    var body: some View {
        NavigationStack {
            Group {
                if let vm {
                    content(vm)
                } else {
                    ProgressView()
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .navigationTitle(String(localized: "tab_settings"))
            .disabled(isProcessing)
            .overlay { if isProcessing { signOutOverlay } }
        }
        .task {
            if vm == nil {
                vm = ProfileViewModel(users: env.userRepository, transfer: env.fileTransfer)
            }
            await vm?.load()
        }
        .onChange(of: avatarItem) { _, newItem in
            guard let newItem else { return }
            Task {
                if let data = try? await newItem.loadTransferable(type: Data.self) {
                    await vm?.setAvatar(data: data)
                }
                avatarItem = nil
            }
        }
    }

    @ViewBuilder
    private func content(_ vm: ProfileViewModel) -> some View {
        ScrollView {
            VStack(spacing: 24) {
                profileHeader(vm)

                VStack(spacing: 8) {
                    NavigationLink {
                        EditProfileScreen(onSaved: { Task { await vm.load() } })
                    } label: {
                        settingsRow(icon: "person.text.rectangle", titleKey: "settings_edit_profile")
                    }
                    .buttonStyle(.plain)

                    NavigationLink {
                        PrivacySettingsScreen()
                    } label: {
                        settingsRow(icon: "lock.shield", titleKey: "settings_privacy")
                    }
                    .buttonStyle(.plain)

                    NavigationLink {
                        DevicesScreen()
                    } label: {
                        settingsRow(icon: "laptopcomputer.and.iphone", titleKey: "settings_devices")
                    }
                    .buttonStyle(.plain)

                    NavigationLink {
                        CacheSettingsScreen()
                    } label: {
                        settingsRow(icon: "internaldrive", titleKey: "settings_cache")
                    }
                    .buttonStyle(.plain)

                    NavigationLink {
                        VaultScreen()
                    } label: {
                        settingsRow(icon: "lock.shield.fill", titleKey: "settings_vault")
                    }
                    .buttonStyle(.plain)
                }

                storageCard(vm)

                accountActions

                if let snackbar = vm.state.snackbar {
                    Text(snackbar)
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.error)
                        .onAppear {
                            Task { @MainActor in
                                try? await Task.sleep(nanoseconds: 2_500_000_000)
                                vm.snackbarShown()
                            }
                        }
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 24)
        }
    }

    @ViewBuilder
    private func profileHeader(_ vm: ProfileViewModel) -> some View {
        VStack(spacing: 12) {
            ZStack(alignment: .bottomTrailing) {
                avatarCircle(vm)
                PhotosPicker(selection: $avatarItem, matching: .images) {
                    Image(systemName: "camera.fill")
                        .font(.system(size: 14))
                        .foregroundStyle(.white)
                        .padding(8)
                        .background(AppColors.accent)
                        .clipShape(Circle())
                }
            }

            Text(verbatim: vm.state.displayName)
                .font(AppTypography.headlineSmall)
            if let user = vm.state.user, !user.username.isEmpty {
                Text(verbatim: "@\(user.username)")
                    .font(AppTypography.bodyMedium)
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
            if let bio = vm.state.user?.bio, !bio.isEmpty {
                Text(verbatim: bio)
                    .font(AppTypography.bodyMedium)
                    .multilineTextAlignment(.center)
                    .foregroundStyle(AppColors.onSurface)
            }
            if vm.state.hasAvatar {
                Button(role: .destructive) {
                    Task { await vm.removeAvatar() }
                } label: {
                    Text("settings_remove_avatar")
                        .font(AppTypography.labelLarge)
                }
            }
        }
        .frame(maxWidth: .infinity)
    }

    @ViewBuilder
    private func avatarCircle(_ vm: ProfileViewModel) -> some View {
        let size: CGFloat = 96
        ZStack {
            if !vm.state.avatarCandidateURLs.isEmpty {
                FallbackRemoteImage(fileId: vm.state.profilePictureFileID, urls: vm.state.avatarCandidateURLs) {
                    placeholderAvatar
                }
                .frame(width: size, height: size)
                .clipShape(Circle())
            } else {
                placeholderAvatar
                    .frame(width: size, height: size)
            }
            if vm.state.isUpdatingAvatar {
                Circle().fill(.black.opacity(0.3)).frame(width: size, height: size)
                ProgressView().tint(.white)
            }
        }
    }

    private var placeholderAvatar: some View {
        Circle()
            .fill(AppColors.accent.opacity(0.15))
            .overlay {
                Image(systemName: "person.fill")
                    .font(.system(size: 40))
                    .foregroundStyle(AppColors.accent)
            }
    }

    @ViewBuilder
    private func storageCard(_ vm: ProfileViewModel) -> some View {
        let used = vm.state.usedStorage
        let limit = vm.state.storageLimit
        let fraction = limit > 0 ? min(1.0, Double(used) / Double(limit)) : 0
        VStack(alignment: .leading, spacing: 8) {
            Text("settings_storage")
                .font(AppTypography.titleSmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .textCase(.uppercase)
            ProgressView(value: fraction)
                .tint(AppColors.accent)
            Text(verbatim: "\(FormatUtils.formatSize(used)) / \(FormatUtils.formatSize(limit))")
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
        }
        .padding(16)
        .background(AppColors.onSurface.opacity(0.04))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }

    @ViewBuilder
    private var accountActions: some View {
        VStack(spacing: 8) {
            Button {
                showSignOutConfirm = true
            } label: {
                settingsRow(icon: "rectangle.portrait.and.arrow.right", titleKey: "settings_sign_out", tint: AppColors.onSurface)
            }
            .buttonStyle(.plain)

            Button(role: .destructive) {
                showDeleteConfirm = true
            } label: {
                settingsRow(icon: "trash", titleKey: "settings_delete_account", tint: AppColors.error)
            }
            .buttonStyle(.plain)
        }
        .confirmationDialog(String(localized: "settings_sign_out_confirm"), isPresented: $showSignOutConfirm, titleVisibility: .visible) {
            Button(String(localized: "settings_sign_out"), role: .destructive) { signOut() }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
        .confirmationDialog(String(localized: "settings_delete_account_confirm"), isPresented: $showDeleteConfirm, titleVisibility: .visible) {
            Button(String(localized: "settings_delete_account"), role: .destructive) { deleteAccount() }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
    }

    @ViewBuilder
    private func settingsRow(icon: String, titleKey: LocalizedStringResource, tint: Color = AppColors.accent) -> some View {
        HStack(spacing: 16) {
            Image(systemName: icon)
                .font(.system(size: 20))
                .frame(width: 36, height: 36)
                .foregroundStyle(tint)
                .background(tint.opacity(0.12))
                .clipShape(RoundedRectangle(cornerRadius: 10))
            Text(titleKey)
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurface)
            Spacer()
            Image(systemName: "chevron.right")
                .foregroundStyle(AppColors.onSurfaceVariant)
        }
        .padding(14)
        .background(AppColors.onSurface.opacity(0.04))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }

    @ViewBuilder
    private var signOutOverlay: some View {
        ZStack {
            Color.black.opacity(0.25).ignoresSafeArea()
            VStack(spacing: 12) {
                ProgressView()
                Text("settings_signing_out")
                    .font(AppTypography.bodyMedium)
                    .foregroundStyle(AppColors.onSurface)
            }
            .padding(24)
            .background(.regularMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 16))
        }
    }

    private func signOut() {
        guard !isProcessing else { return }
        isProcessing = true
        Task {
            await env.signOut()
            onSignOut()
        }
    }

    private func deleteAccount() {
        guard !isProcessing else { return }
        isProcessing = true
        Task {
            do {
                try await env.userRepository.deleteAccount()
            } catch {
                // Даже при ошибке очищаем локально (аккаунт мог быть удалён).
            }
            // Аккаунт удалён — серверный отзыв сессии не нужен, только локальная очистка.
            await env.resetLocalState()
            onSignOut()
        }
    }
}
