import SwiftUI
import GRPCCore
import BarkCloudKit

@MainActor
@Observable
final class EditProfileViewModel {
    struct UiState {
        var firstName = ""
        var lastName = ""
        var username = ""
        var bio = ""
        var originalUsername = ""
        var isLoading = true
        var isSaving = false
        var usernameError: String?
        var bioError: String?
        var snackbar: String?
    }

    let bioLimit = 200
    var state = UiState()
    private let users: UserRepository

    init(users: UserRepository) { self.users = users }

    func load() async {
        do {
            let user = try await users.getUser()
            state.firstName = user.firstName
            state.lastName = user.lastName
            state.username = user.username
            state.originalUsername = user.username
            state.bio = user.bio
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoading = false
    }

    func onBioChange(_ value: String) {
        state.bio = String(value.prefix(bioLimit))
        state.bioError = nil
    }

    /// Сохраняет правки. Возвращает `true` при успехе (для возврата назад).
    func save() async -> Bool {
        state.isSaving = true
        state.usernameError = nil
        state.bioError = nil
        defer { state.isSaving = false }

        do {
            try await users.changeName(
                firstName: state.firstName.trimmingCharacters(in: .whitespaces),
                lastName: state.lastName.trimmingCharacters(in: .whitespaces)
            )

            let newUsername = state.username.trimmingCharacters(in: .whitespaces)
            if newUsername != state.originalUsername, !newUsername.isEmpty {
                if (try? await users.usernameExists(newUsername)) == true {
                    state.usernameError = String(localized: "edit_username_taken")
                    return false
                }
                try await users.changeUsername(newUsername)
            }

            try await users.changeBio(state.bio)
            return true
        } catch let err as RPCError {
            let code = err.errorCode?.uppercased() ?? ""
            switch code {
            case DomainErrorCodes.usernameReserved:
                state.usernameError = String(localized: "err_username_reserved")
            case DomainErrorCodes.bioTooLong:
                state.bioError = String(localized: "err_bio_too_long")
            default:
                state.snackbar = domainErrorMessage(err)
            }
            return false
        } catch {
            state.snackbar = domainErrorMessage(error)
            return false
        }
    }
}

struct EditProfileScreen: View {
    @Environment(AppEnvironment.self) private var env
    @Environment(\.dismiss) private var dismiss
    let onSaved: () -> Void

    @State private var vm: EditProfileViewModel?

    var body: some View {
        Group {
            if let vm {
                form(vm)
            } else {
                ProgressView()
            }
        }
        .navigationTitle(String(localized: "settings_edit_profile"))
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if vm == nil {
                vm = EditProfileViewModel(users: env.userRepository)
            }
            await vm?.load()
        }
    }

    @ViewBuilder
    private func form(_ vm: EditProfileViewModel) -> some View {
        Form {
            Section(String(localized: "edit_section_name")) {
                TextField(String(localized: "edit_first_name"), text: Binding(
                    get: { vm.state.firstName }, set: { vm.state.firstName = $0 }))
                TextField(String(localized: "edit_last_name"), text: Binding(
                    get: { vm.state.lastName }, set: { vm.state.lastName = $0 }))
            }

            Section {
                TextField(String(localized: "edit_username"), text: Binding(
                    get: { vm.state.username },
                    set: { vm.state.username = $0; vm.state.usernameError = nil }))
                .autocorrectionDisabled()
                .textInputAutocapitalization(.never)
            } header: {
                Text("edit_username")
            } footer: {
                if let err = vm.state.usernameError {
                    Text(err).foregroundStyle(AppColors.error)
                }
            }

            Section {
                TextField(String(localized: "edit_bio"), text: Binding(
                    get: { vm.state.bio }, set: { vm.onBioChange($0) }), axis: .vertical)
                .lineLimit(3...6)
            } header: {
                Text("edit_bio")
            } footer: {
                if let err = vm.state.bioError {
                    Text(err).foregroundStyle(AppColors.error)
                } else {
                    Text(verbatim: "\(vm.state.bio.count)/\(vm.bioLimit)")
                }
            }

            if let snackbar = vm.state.snackbar {
                Text(snackbar).foregroundStyle(AppColors.error)
            }
        }
        .disabled(vm.state.isSaving)
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button {
                    Task {
                        if await vm.save() {
                            onSaved()
                            dismiss()
                        }
                    }
                } label: {
                    if vm.state.isSaving {
                        ProgressView()
                    } else {
                        Text("action_save")
                    }
                }
                .disabled(vm.state.isSaving)
            }
        }
    }
}
