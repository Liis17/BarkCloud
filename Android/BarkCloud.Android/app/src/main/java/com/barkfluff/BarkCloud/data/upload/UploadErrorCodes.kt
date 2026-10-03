package com.barkfluff.BarkCloud.data.upload

/** GUID-коды доменных ошибок Upload 2.0 (трейлер x-error-code). */
object UploadErrorCodes {
    /** Файл уже прикреплён к директории — идемпотентный replay attach. */
    const val FILE_ALREADY_ATTACHED = "F1A2B3C4-5D6E-47F8-9A0B-1C2D3E4F5A6B"

    /** Complete: сервер не видит всех частей. */
    const val UPLOAD_PARTS_INCOMPLETE = "09BF4D7B-7DB9-4284-BDB0-85457B22A589"

    /** Превышена квота хранилища при создании сессии. */
    const val QUOTA_EXCEEDED = "64E94D14-CA45-4827-876C-701FE21986B2"
}
