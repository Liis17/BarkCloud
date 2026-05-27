package com.barkfluff.BarkCloud.grpc

import android.net.Uri
import com.barkfluff.BarkCloud.BuildConfig

/**
 * Конфигурация эндпоинтов файлового сервиса. nginx терминирует TLS и маршрутизирует
 * gRPC по портам (Identity :7020, Users :7021, Files :7025). HTTP-раздача файлов —
 * `:7025/web/{upload|download}/{id}`.
 */
object GrpcEndpoint {

    /** База HTTP-раздачи файлов (`/web/download/{id}`, `/web/upload/{id}`). */
    val filesWebBase: String get() = BuildConfig.FILES_WEB_BASE

    /**
     * Перестраивает ссылку скачивания файла на актуальный эндпоинт Files. Часть
     * ссылок (например, URL аватара) хранится в БД и могла быть сгенерирована при
     * прежней конфигурации хоста — она может указывать на недостижимый/устаревший
     * адрес. Берём идентификатор из пути `.../download/{id}` и собираем ссылку заново.
     * Если путь не похож на ссылку скачивания — возвращаем исходный URL.
     */
    fun normalizedFileDownloadURL(raw: String?): String? {
        if (raw.isNullOrEmpty()) return null
        val uri = runCatching { Uri.parse(raw) }.getOrNull() ?: return raw
        val segments = uri.pathSegments
        val idx = segments.lastIndexOf("download")
        if (idx >= 0 && idx + 1 < segments.size) {
            return "$filesWebBase/download/${segments[idx + 1]}"
        }
        return raw
    }
}
