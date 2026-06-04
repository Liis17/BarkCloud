import Foundation
import GRPCCore

/// Доступ к сервису Users (профиль, приватность, устройства, аккаунт) + установка
/// аватара (через `FileTransferService`). Методы пробрасывают `RPCError` —
/// доменные ошибки маппит UI через `domainErrorMessage(_:)`.
public final class UserRepository: Sendable {
    private let grpc: GrpcManager
    private let transfer: FileTransferService

    public init(grpc: GrpcManager, transfer: FileTransferService) {
        self.grpc = grpc
        self.transfer = transfer
    }

    // MARK: - Профиль

    /// Профиль пользователя. `userID = 0` — свой профиль.
    public func getUser(userID: Int64 = 0) async throws -> Barkcloud_Users_User {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_GetUserRequest()
        req.userID = userID
        return try await stub.getUser(req).user
    }

    public func changeName(firstName: String, lastName: String) async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_ChangeNameRequest()
        req.firstName = firstName
        req.lastName = lastName
        _ = try await stub.changeName(req)
    }

    public func usernameExists(_ username: String) async throws -> Bool {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_CheckExistUsernameRequest()
        req.username = username
        return try await stub.checkExistUsername(req).exist
    }

    public func changeUsername(_ username: String) async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_ChangeUsernameRequest()
        req.username = username
        _ = try await stub.changeUsername(req)
    }

    public func changeBio(_ bio: String) async throws {
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
    public func searchUsers(query: String, limit: Int = 20) async throws -> [CloudUser] {
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

    public func getPrivacySettings() async throws -> Barkcloud_Users_PrivacySettings {
        let stub = try await grpc.usersStub()
        return try await stub.getPrivacySettings(Barkcloud_Users_GetPrivacySettingsRequest()).settings
    }

    @discardableResult
    public func updatePrivacySettings(_ settings: Barkcloud_Users_PrivacySettings) async throws -> Barkcloud_Users_PrivacySettings {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_UpdatePrivacySettingsRequest()
        req.settings = settings
        return try await stub.updatePrivacySettings(req).settings
    }

    // MARK: - Устройства

    public func getDevices() async throws -> [Barkcloud_Users_Device] {
        let stub = try await grpc.usersStub()
        return try await stub.getDevices(Barkcloud_Users_GetDevicesRequest()).devices
    }

    public func getCurrentDevice() async throws -> Barkcloud_Users_Device? {
        let stub = try await grpc.usersStub()
        let resp = try await stub.getCurrentDevice(Barkcloud_Users_GetCurrentDeviceRequest())
        return resp.hasDevice ? resp.device : nil
    }

    public func renameDevice(deviceID: String, customName: String) async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_RenameDeviceRequest()
        req.deviceID = deviceID
        req.customName = customName
        _ = try await stub.renameDevice(req)
    }

    public func deleteDevice(deviceID: String) async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_DeleteDeviceRequest()
        req.deviceID = deviceID
        _ = try await stub.deleteDevice(req)
    }

    // MARK: - Аккаунт

    public func deleteAccount() async throws {
        let stub = try await grpc.usersStub()
        _ = try await stub.deleteAccount(Barkcloud_Users_DeleteAccountRequest())
    }

    // MARK: - Аватар (двухшаговый флоу)

    /// Загрузить картинку как `USER_AVATAR` и установить аватаром.
    public func setAvatar(imageData: Data, fileName: String) async throws {
        let upload = try await transfer.getUploadURL(type: .userAvatar)
        let fileID = try await transfer.upload(data: imageData, fileName: fileName, to: upload.url)
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_SetProfilePictureRequest()
        req.fileID = fileID
        _ = try await stub.setProfilePicture(req)
    }

    /// Удалить аватар (`SetProfilePicture("")`).
    public func removeAvatar() async throws {
        let stub = try await grpc.usersStub()
        var req = Barkcloud_Users_SetProfilePictureRequest()
        req.fileID = ""
        _ = try await stub.setProfilePicture(req)
    }
}
