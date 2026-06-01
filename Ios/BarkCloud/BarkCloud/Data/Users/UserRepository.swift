import Foundation
import GRPCCore

/// Доступ к сервису Users (профиль, приватность, устройства, аккаунт) + установка
/// аватара (через `FileTransferService`). Методы пробрасывают `RPCError` —
/// доменные ошибки маппит UI через `domainErrorMessage(_:)`.
final class UserRepository: Sendable {
    private let grpc: GrpcManager
    private let transfer: FileTransferService

    init(grpc: GrpcManager, transfer: FileTransferService) {
        self.grpc = grpc
        self.transfer = transfer
    }

    // MARK: - Профиль

    /// Профиль пользователя. `userID = 0` — свой профиль.
    func getUser(userID: Int64 = 0) async throws -> Barkcloud_Users_User {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_GetUserRequest()
        req.userID = userID
        return try await stub.getUser(req).user
    }

    func changeName(firstName: String, lastName: String) async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_ChangeNameRequest()
        req.firstName = firstName
        req.lastName = lastName
        _ = try await stub.changeName(req)
    }

    func usernameExists(_ username: String) async throws -> Bool {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_CheckExistUsernameRequest()
        req.username = username
        return try await stub.checkExistUsername(req).exist
    }

    func changeUsername(_ username: String) async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_ChangeUsernameRequest()
        req.username = username
        _ = try await stub.changeUsername(req)
    }

    func changeBio(_ bio: String) async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_ChangeBioRequest()
        req.bio = bio
        _ = try await stub.changeBio(req)
    }

    // MARK: - Поиск

    /// Поиск пользователей по юзернейму/имени. Сервер требует минимум 2 символа,
    /// поэтому короткие запросы заворачиваем тут же без сетевого вызова.
    /// `limit` 1..50 (default 20). Возвращает только тех, у кого
    /// `PrivacySettings.searchableByUsername == true`.
    func searchUsers(query: String, limit: Int = 20) async throws -> [CloudUser] {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 2 else { return [] }
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_SearchUsersRequest()
        req.query = trimmed
        req.limit = Int32(max(1, min(50, limit)))
        let resp = try await stub.searchUsers(req)
        return resp.users.map(CloudUser.init)
    }

    // MARK: - Приватность

    func getPrivacySettings() async throws -> Barkcloud_Users_PrivacySettings {
        let stub = try await grpc.usersStub()
        return try await stub.getPrivacySettings(Barkcloud_Users_GetPrivacySettingsRequest()).settings
    }

    @discardableResult
    func updatePrivacySettings(_ settings: Barkcloud_Users_PrivacySettings) async throws -> Barkcloud_Users_PrivacySettings {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_UpdatePrivacySettingsRequest()
        req.settings = settings
        return try await stub.updatePrivacySettings(req).settings
    }

    // MARK: - Устройства

    func getDevices() async throws -> [Barkcloud_Users_Device] {
        let stub = try await grpc.usersStub()
        return try await stub.getDevices(Barkcloud_Users_GetDevicesRequest()).devices
    }

    func getCurrentDevice() async throws -> Barkcloud_Users_Device? {
        let stub = try await grpc.usersStub()
        let resp = try await stub.getCurrentDevice(Barkcloud_Users_GetCurrentDeviceRequest())
        return resp.hasDevice ? resp.device : nil
    }

    func renameDevice(deviceID: String, customName: String) async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_RenameDeviceRequest()
        req.deviceID = deviceID
        req.customName = customName
        _ = try await stub.renameDevice(req)
    }

    func deleteDevice(deviceID: String) async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_DeleteDeviceRequest()
        req.deviceID = deviceID
        _ = try await stub.deleteDevice(req)
    }

    // MARK: - Аккаунт

    func deleteAccount() async throws {
        let stub = try await grpc.usersStub()
        _ = try await stub.deleteAccount(Barkcloud_Users_DeleteAccountRequest())
    }

    // MARK: - Аватар (двухшаговый флоу)

    /// Загрузить картинку как `USER_AVATAR` и установить аватаром.
    func setAvatar(imageData: Data, fileName: String) async throws {
        let upload = try await transfer.getUploadURL(type: .userAvatar)
        let fileID = try await transfer.upload(data: imageData, fileName: fileName, to: upload.url)
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_SetProfilePictureRequest()
        req.fileID = fileID
        _ = try await stub.setProfilePicture(req)
    }

    /// Удалить аватар (`SetProfilePicture("")`).
    func removeAvatar() async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_SetProfilePictureRequest()
        req.fileID = ""
        _ = try await stub.setProfilePicture(req)
    }
}
