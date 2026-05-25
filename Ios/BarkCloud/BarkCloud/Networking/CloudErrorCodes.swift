import Foundation
import GRPCCore

/// GUID-коды доменных ошибок сервисов Files и Users. Приходят как gRPC
/// `FailedPrecondition` с `x-error-code` в trailing-метадате (см. `RPCError.errorCode`).
/// Источник: backend `Shared/BarkCloud.Shared.Exceptions`.
enum DomainErrorCodes {
    // Files / Cloud
    static let fileNotFound = "91E25C73-FC80-43C1-893D-F26F39726F03"
    static let fileEntryNotFound = "8E3A9C12-5F1D-4D32-B7F4-1A2B3C4D5E6F"
    static let fileAlreadyAttached = "F1A2B3C4-5D6E-47F8-9A0B-1C2D3E4F5A6B"
    static let directoryNameConflict = "C0A4E97C-2E73-4D5D-AB1A-7E3DC8A1D8F1"
    static let directoryNotFound = "B86C8B6E-2A12-44E1-9B6B-1AE0F1C0E1AE"
    static let albumNameConflict = "B1C3D5E7-9F0A-42B4-86C8-1D3E5F7A9B0C"
    static let albumNotFound = "C2D4E6F8-1A3B-45C7-89D0-2E4F6A8B0C1D"
    static let circularMove = "F2B82A3C-1D2E-4B0E-9A19-2D90F8B7C001"
    static let cloudAccessDenied = "D4F1E2A8-9C5B-4A77-83BB-5E6F7A8B9C01"
    static let invalidThumbnailSource = "A7E3D1C9-4B2F-48A6-9C7D-2E1F3A4B5C6D"
    static let notValidFileID = "D10BD126-48EA-4D11-9CFF-4C2FDD6F9899"
    // Users
    static let usernameReserved = "A3F1B2C4-7D8E-4F5A-9B6C-1E2D3F4A5B6C"
    static let bioTooLong = "1A652492-87A4-4B8B-B758-E7FBE1F39DDF"
    static let profilePictureHasNotValidType = "7097703F-977C-4E28-8C85-1A287B3FF8AD"
    static let userIsDraft = "91D75288-8314-4658-AA0E-EC1D01779D58"

    private static let messages: [String: String.LocalizationValue] = [
        fileAlreadyAttached: "err_file_already_attached",
        directoryNameConflict: "err_name_conflict",
        albumNameConflict: "err_album_name_conflict",
        cloudAccessDenied: "err_access_denied",
        circularMove: "err_circular_move",
        invalidThumbnailSource: "err_invalid_thumbnail",
        usernameReserved: "err_username_reserved",
        bioTooLong: "err_bio_too_long",
        profilePictureHasNotValidType: "err_avatar_invalid_type"
    ]

    /// Локализованное сообщение для известного кода ошибки, иначе `nil`.
    static func localizedMessage(for code: String?) -> String? {
        guard let code = code?.uppercased(), let key = messages[code] else { return nil }
        return String(localized: key)
    }
}

/// Пользовательское сообщение для произвольной ошибки репозитория: доменное (по
/// `x-error-code`), затем текст gRPC, иначе общий fallback.
func domainErrorMessage(_ error: Error) -> String {
    if let rpc = error as? RPCError {
        if let mapped = DomainErrorCodes.localizedMessage(for: rpc.errorCode) {
            return mapped
        }
        if !rpc.message.isEmpty {
            return rpc.message
        }
    }
    return String(localized: "error_generic")
}
