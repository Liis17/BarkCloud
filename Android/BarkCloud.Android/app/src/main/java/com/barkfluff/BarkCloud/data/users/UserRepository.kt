package com.barkfluff.BarkCloud.data.users

import android.net.Uri
import barkcloud.files.FilesApiOuterClass.UploadFileType
import barkcloud.users.UsersApiOuterClass.ChangeBioRequest
import barkcloud.users.UsersApiOuterClass.ChangeNameRequest
import barkcloud.users.UsersApiOuterClass.ChangeUsernameRequest
import barkcloud.users.UsersApiOuterClass.CheckExistUsernameRequest
import barkcloud.users.UsersApiOuterClass.DeleteAccountRequest
import barkcloud.users.UsersApiOuterClass.DeleteDeviceRequest
import barkcloud.users.UsersApiOuterClass.Device
import barkcloud.users.UsersApiOuterClass.GetCurrentDeviceRequest
import barkcloud.users.UsersApiOuterClass.GetDevicesRequest
import barkcloud.users.UsersApiOuterClass.GetPrivacySettingsRequest
import barkcloud.users.UsersApiOuterClass.GetUserRequest
import barkcloud.users.UsersApiOuterClass.PrivacySettings
import barkcloud.users.UsersApiOuterClass.RenameDeviceRequest
import barkcloud.users.UsersApiOuterClass.SetProfilePictureRequest
import barkcloud.users.UsersApiOuterClass.UpdatePrivacySettingsRequest
import barkcloud.users.UsersApiOuterClass.User
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.net.FileTransferService

/**
 * Доступ к сервису Users (профиль, приватность, устройства, аккаунт) + установка
 * аватара (через [FileTransferService]). Методы возвращают proto-типы — UI читает поля
 * напрямую (как в iOS).
 */
class UserRepository(
    private val grpc: GrpcManager,
    private val transfer: FileTransferService,
) {

    // MARK: Профиль

    /** Профиль пользователя. `userId = 0` — свой профиль. */
    suspend fun getUser(userId: Long = 0): User =
        grpc.usersStub().getUser(GetUserRequest.newBuilder().setUserId(userId).build()).user

    suspend fun changeName(firstName: String, lastName: String) {
        grpc.usersStub().changeName(
            ChangeNameRequest.newBuilder().setFirstName(firstName).setLastName(lastName).build()
        )
    }

    suspend fun usernameExists(username: String): Boolean =
        grpc.usersStub().checkExistUsername(
            CheckExistUsernameRequest.newBuilder().setUsername(username).build()
        ).exist

    suspend fun changeUsername(username: String) {
        grpc.usersStub().changeUsername(
            ChangeUsernameRequest.newBuilder().setUsername(username).build()
        )
    }

    suspend fun changeBio(bio: String) {
        grpc.usersStub().changeBio(ChangeBioRequest.newBuilder().setBio(bio).build())
    }

    // MARK: Приватность

    suspend fun getPrivacySettings(): PrivacySettings =
        grpc.usersStub().getPrivacySettings(GetPrivacySettingsRequest.getDefaultInstance()).settings

    suspend fun updatePrivacySettings(settings: PrivacySettings): PrivacySettings =
        grpc.usersStub().updatePrivacySettings(
            UpdatePrivacySettingsRequest.newBuilder().setSettings(settings).build()
        ).settings

    // MARK: Устройства

    suspend fun getDevices(): List<Device> =
        grpc.usersStub().getDevices(GetDevicesRequest.getDefaultInstance()).devicesList

    suspend fun getCurrentDevice(): Device? {
        val resp = grpc.usersStub().getCurrentDevice(GetCurrentDeviceRequest.getDefaultInstance())
        return if (resp.hasDevice()) resp.device else null
    }

    suspend fun renameDevice(deviceId: String, customName: String) {
        grpc.usersStub().renameDevice(
            RenameDeviceRequest.newBuilder().setDeviceId(deviceId).setCustomName(customName).build()
        )
    }

    suspend fun deleteDevice(deviceId: String) {
        grpc.usersStub().deleteDevice(DeleteDeviceRequest.newBuilder().setDeviceId(deviceId).build())
    }

    // MARK: Аккаунт

    suspend fun deleteAccount() {
        grpc.usersStub().deleteAccount(DeleteAccountRequest.getDefaultInstance())
    }

    // MARK: Аватар (двухшаговый флоу)

    /** Загрузить картинку как `USER_AVATAR` и установить аватаром. */
    suspend fun setAvatar(uri: Uri, fileName: String) {
        val target = transfer.getUploadUrl(UploadFileType.USER_AVATAR)
        val fileId = transfer.upload(uri, fileName, target.url)
        grpc.usersStub().setProfilePicture(
            SetProfilePictureRequest.newBuilder().setFileId(fileId).build()
        )
    }

    /** Удалить аватар (`SetProfilePicture("")`). */
    suspend fun removeAvatar() {
        grpc.usersStub().setProfilePicture(
            SetProfilePictureRequest.newBuilder().setFileId("").build()
        )
    }
}
