import Foundation
import Observation
import BarkCloudKit

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

        /// Кандидаты для загрузки аватара по приоритету: сначала превью (легче),
        /// затем полное изображение. Каждый URL нормализуется на актуальный хост
        /// Files (сохранённая в БД ссылка могла указывать на устаревший хост).
        var avatarCandidateURLs: [URL] {
            guard let user else { return [] }
            var result: [URL] = []
            for raw in [user.profilePicturePreview, user.profilePicture] {
                if let url = GrpcEndpoint.normalizedFileDownloadURL(raw), !result.contains(url) {
                    result.append(url)
                }
            }
            return result
        }

        /// file_id аватара — последний сегмент download-ссылки. Ключ дискового кеша
        /// аватара; при смене картинки меняется и id, поэтому старый кеш не мешает.
        var profilePictureFileID: String? {
            guard let user, !user.profilePicture.isEmpty,
                  let comps = URLComponents(string: user.profilePicture) else { return nil }
            let parts = comps.path.split(separator: "/").map(String.init)
            if let idx = parts.lastIndex(of: "download"), idx + 1 < parts.count {
                return parts[idx + 1]
            }
            return nil
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
