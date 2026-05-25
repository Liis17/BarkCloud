import SwiftUI

@MainActor
@Observable
final class PrivacySettingsViewModel {
    struct UiState {
        var settings = Barkcloud_Users_PrivacySettings()
        var isLoading = true
        var isSaving = false
        var snackbar: String?
    }

    var state = UiState()
    private let users: UserRepository

    init(users: UserRepository) { self.users = users }

    func load() async {
        do {
            state.settings = try await users.getPrivacySettings()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoading = false
    }

    /// Применяет изменение локально и отправляет объект целиком.
    func apply(_ mutate: @escaping (inout Barkcloud_Users_PrivacySettings) -> Void) {
        mutate(&state.settings)
        let snapshot = state.settings
        Task {
            state.isSaving = true
            do {
                state.settings = try await users.updatePrivacySettings(snapshot)
            } catch {
                state.snackbar = domainErrorMessage(error)
            }
            state.isSaving = false
        }
    }
}

struct PrivacySettingsScreen: View {
    @Environment(AppEnvironment.self) private var env
    @State private var vm: PrivacySettingsViewModel?

    private let visibilityOptions: [Barkcloud_Users_PrivacyVisibility] = [.everyone, .contacts, .nobody]

    var body: some View {
        Group {
            if let vm {
                form(vm)
            } else {
                ProgressView()
            }
        }
        .navigationTitle(String(localized: "settings_privacy"))
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if vm == nil {
                vm = PrivacySettingsViewModel(users: env.userRepository)
            }
            await vm?.load()
        }
    }

    @ViewBuilder
    private func form(_ vm: PrivacySettingsViewModel) -> some View {
        Form {
            Section(String(localized: "privacy_section_visibility")) {
                visibilityPicker(titleKey: "privacy_profile", value: vm.state.settings.profileVisibility) { v in
                    vm.apply { $0.profileVisibility = v }
                }
                visibilityPicker(titleKey: "privacy_email", value: vm.state.settings.emailVisibility) { v in
                    vm.apply { $0.emailVisibility = v }
                }
                visibilityPicker(titleKey: "privacy_last_seen", value: vm.state.settings.lastSeenVisibility) { v in
                    vm.apply { $0.lastSeenVisibility = v }
                }
            }

            Section {
                Toggle(isOn: Binding(
                    get: { vm.state.settings.searchableByUsername },
                    set: { newValue in vm.apply { $0.searchableByUsername = newValue } }
                )) {
                    Text("privacy_searchable")
                }
            } footer: {
                Text("privacy_searchable_hint")
            }

            if let snackbar = vm.state.snackbar {
                Text(snackbar).foregroundStyle(AppColors.error)
            }
        }
        .disabled(vm.state.isSaving)
    }

    @ViewBuilder
    private func visibilityPicker(
        titleKey: LocalizedStringResource,
        value: Barkcloud_Users_PrivacyVisibility,
        onChange: @escaping (Barkcloud_Users_PrivacyVisibility) -> Void
    ) -> some View {
        Picker(selection: Binding(get: { value }, set: { onChange($0) })) {
            ForEach(visibilityOptions, id: \.self) { option in
                Text(Self.label(option)).tag(option)
            }
        } label: {
            Text(titleKey)
        }
    }

    private static func label(_ v: Barkcloud_Users_PrivacyVisibility) -> LocalizedStringResource {
        switch v {
        case .everyone: return "privacy_everyone"
        case .contacts: return "privacy_contacts"
        case .nobody: return "privacy_nobody"
        default: return "privacy_everyone"
        }
    }
}
