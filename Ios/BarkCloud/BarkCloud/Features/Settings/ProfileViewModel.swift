import Foundation
import Observation

@MainActor
@Observable
final class ProfileViewModel {
    struct UiState {
        var user: Barkcloud_Users_User?
        var usedStorage: Int64 = 0
        var storageLimit: Int64 = 0
        var isLoading = true
        var isUpdatingAvatar = false
        var snackbar: String?

        var displayName: String {
            guard let user else { return "" }
            let name = "\(user.firstName) \(user.lastName)".trimmingCharacters(in: .whitespaces)
            return name.isEmpty ? user.username : name
        }

        var hasAvatar: Bool {
            !(user?.profilePicture.isEmpty ?? true)
        }

        var avatarURL: URL? {
            guard let user else { return nil }
            let raw = user.profilePicturePreview.isEmpty ? user.profilePicture : user.profilePicturePreview
            return raw.isEmpty ? nil : URL(string: raw)
        }
    }

    var state = UiState()

    private let users: UserRepository
    private let transfer: FileTransferService

    init(users: UserRepository, transfer: FileTransferService) {
        self.users = users
        self.transfer = transfer
    }

    func load() async {
        state.isLoading = true
        do {
            state.user = try await users.getUser()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        // Квота — best-effort, не блокирует профиль.
        if let storage = try? await transfer.storageInfo() {
            state.usedStorage = storage.used
            state.storageLimit = storage.limit
        }
        state.isLoading = false
    }

    func setAvatar(data: Data) async {
        state.isUpdatingAvatar = true
        do {
            try await users.setAvatar(imageData: data, fileName: "avatar.jpg")
            state.user = try await users.getUser()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isUpdatingAvatar = false
    }

    func removeAvatar() async {
        state.isUpdatingAvatar = true
        do {
            try await users.removeAvatar()
            state.user = try await users.getUser()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isUpdatingAvatar = false
    }

    func snackbarShown() { state.snackbar = nil }
}
