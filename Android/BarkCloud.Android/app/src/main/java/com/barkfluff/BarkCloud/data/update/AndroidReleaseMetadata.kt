package com.barkfluff.BarkCloud.data.update

import org.json.JSONArray
import org.json.JSONObject
import java.math.BigInteger

data class ReleaseAsset(val name: String, val size: Long)

data class LatestGithubRelease(
    val tag: String,
    val notes: String,
    val assets: List<ReleaseAsset>,
)

data class AndroidReleaseMetadata(
    val tag: String,
    val versionName: String,
    val versionCode: Long,
    val apkFileName: String,
    val apkSize: Long,
    val sha256: String,
    val notes: String,
) {
    val downloadUrl: String
        get() = "https://github.com/Liis17/BarkCloud/releases/download/$tag/$apkFileName"

    fun toJson(): String = JSONObject()
        .put("tag", tag)
        .put("versionName", versionName)
        .put("versionCode", versionCode)
        .put("apkFileName", apkFileName)
        .put("apkSize", apkSize)
        .put("sha256", sha256)
        .put("notes", notes)
        .toString()

    companion object {
        fun fromJson(json: String): AndroidReleaseMetadata {
            if (json.toByteArray(Charsets.UTF_8).size > AndroidReleaseParser.MAX_JSON_BYTES) {
                throw InvalidReleaseMetadataException("Сохранённые метаданные обновления превышают 1 MiB.")
            }
            val value = JSONObject(json)
            val metadata = AndroidReleaseMetadata(
                tag = value.getString("tag"),
                versionName = value.getString("versionName"),
                versionCode = value.getLong("versionCode"),
                apkFileName = value.getString("apkFileName"),
                apkSize = value.getLong("apkSize"),
                sha256 = value.getString("sha256"),
                notes = value.optString("notes", ""),
            )
            if (metadata.versionName != metadata.tag.removePrefix("v") ||
                metadata.versionCode != AndroidReleaseParser.versionCode(metadata.tag) ||
                metadata.apkFileName != "BarkCloud.Android-${metadata.tag}.apk" ||
                metadata.apkSize <= 0 || metadata.apkSize > AndroidReleaseParser.MAX_APK_BYTES ||
                !Regex("^[0-9a-fA-F]{64}$").matches(metadata.sha256)
            ) {
                throw InvalidReleaseMetadataException("Сохранённые метаданные обновления некорректны.")
            }
            return metadata
        }
    }
}

class MissingUpdaterMetadataException(val latestTag: String? = null) : IllegalArgumentException(
    "Автоматические обновления недоступны для этого релиза. Установите APK вручную из GitHub Releases.",
)

class InvalidReleaseMetadataException(message: String) : IllegalArgumentException(message)

object AndroidReleaseParser {
    const val MAX_JSON_BYTES = 1024 * 1024
    const val MANIFEST_NAME = "android-update.json"
    const val MAX_APK_BYTES = 512L * 1024 * 1024
    private val stableTag = Regex("^v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")
    private val digestPattern = Regex("^[0-9a-fA-F]{64}$")

    fun versionCode(tag: String): Long {
        val match = stableTag.matchEntire(tag)
            ?: throw InvalidReleaseMetadataException("Некорректный тег релиза: $tag")
        val major = match.groupValues[1].toBigInteger()
        val minor = match.groupValues[2].toBigInteger()
        val patch = match.groupValues[3].toBigInteger()
        if (minor > BigInteger.valueOf(999) || patch > BigInteger.valueOf(999)) {
            throw InvalidReleaseMetadataException("Minor и patch версии не могут превышать 999.")
        }
        val code = major * BigInteger.valueOf(1_000_000) +
            minor * BigInteger.valueOf(1_000) + patch
        if (code <= BigInteger.ZERO || code > BigInteger.valueOf(Int.MAX_VALUE.toLong())) {
            throw InvalidReleaseMetadataException("versionCode выходит за допустимый диапазон Android.")
        }
        return code.toLong()
    }

    fun parseLatest(json: String): LatestGithubRelease {
        requireBounded(json, "GitHub release")
        val root = try {
            JSONObject(json)
        } catch (error: Exception) {
            throw InvalidReleaseMetadataException("GitHub вернул некорректные метаданные релиза.")
        }
        val tag = string(root, "tag_name")
        versionCode(tag)
        if (boolean(root, "draft") || boolean(root, "prerelease")) {
            throw InvalidReleaseMetadataException("Можно устанавливать только стабильные опубликованные релизы.")
        }
        val notes = when (val body = root.opt("body")) {
            JSONObject.NULL, null -> ""
            is String -> body
            else -> throw InvalidReleaseMetadataException("Некорректные release notes.")
        }
        val assetsJson = root.opt("assets") as? JSONArray
            ?: throw InvalidReleaseMetadataException("В релизе отсутствует список файлов.")
        val assets = (0 until assetsJson.length()).map { index ->
            val asset = assetsJson.optJSONObject(index)
                ?: throw InvalidReleaseMetadataException("Некорректный файл релиза.")
            ReleaseAsset(
                name = string(asset, "name"),
                size = integer(asset, "size").also {
                    if (it <= 0) throw InvalidReleaseMetadataException("Некорректный размер файла релиза.")
                },
            )
        }
        return LatestGithubRelease(tag, notes, assets)
    }

    fun parseManifest(bytes: ByteArray, release: LatestGithubRelease): AndroidReleaseMetadata {
        if (bytes.size > MAX_JSON_BYTES) {
            throw InvalidReleaseMetadataException("Манифест обновления превышает 1 MiB.")
        }
        val manifestAsset = release.assets.singleOrNull { it.name == MANIFEST_NAME }
            ?: throw MissingUpdaterMetadataException(release.tag)
        if (manifestAsset.size != bytes.size.toLong()) {
            throw InvalidReleaseMetadataException("Размер манифеста не совпадает с GitHub Releases.")
        }
        val root = try {
            JSONObject(bytes.toString(Charsets.UTF_8))
        } catch (error: Exception) {
            throw InvalidReleaseMetadataException("Манифест обновления повреждён.")
        }
        exactKeys(root, setOf("schemaVersion", "tag", "versionName", "versionCode", "apk"))
        if (integer(root, "schemaVersion") != 1L) {
            throw InvalidReleaseMetadataException("Версия схемы манифеста не поддерживается.")
        }
        val tag = string(root, "tag")
        if (tag != release.tag) throw InvalidReleaseMetadataException("Тег манифеста не совпадает с релизом.")
        val expectedCode = versionCode(tag)
        val versionName = string(root, "versionName")
        if (versionName != tag.removePrefix("v")) {
            throw InvalidReleaseMetadataException("versionName не совпадает с тегом релиза.")
        }
        val code = integer(root, "versionCode")
        if (code != expectedCode) throw InvalidReleaseMetadataException("versionCode не совпадает с тегом релиза.")

        val apk = root.optJSONObject("apk")
            ?: throw InvalidReleaseMetadataException("В манифесте отсутствуют данные APK.")
        exactKeys(apk, setOf("fileName", "size", "sha256"))
        val fileName = string(apk, "fileName")
        val expectedName = "BarkCloud.Android-$tag.apk"
        if (fileName != expectedName) throw InvalidReleaseMetadataException("Имя APK не совпадает с тегом релиза.")
        val apkSize = integer(apk, "size")
        if (apkSize <= 0 || apkSize > MAX_APK_BYTES) {
            throw InvalidReleaseMetadataException("Размер APK выходит за допустимые пределы.")
        }
        val apiApk = release.assets.singleOrNull { it.name == expectedName }
            ?: throw InvalidReleaseMetadataException("APK манифеста отсутствует среди файлов релиза.")
        if (apiApk.size != apkSize) {
            throw InvalidReleaseMetadataException("Размер APK не совпадает с GitHub Releases.")
        }
        val digest = string(apk, "sha256")
        if (!digestPattern.matches(digest)) throw InvalidReleaseMetadataException("Некорректный SHA-256 APK.")
        return AndroidReleaseMetadata(
            tag = tag,
            versionName = versionName,
            versionCode = code,
            apkFileName = fileName,
            apkSize = apkSize,
            sha256 = digest.lowercase(),
            notes = release.notes,
        )
    }

    private fun requireBounded(json: String, name: String) {
        if (json.toByteArray(Charsets.UTF_8).size > MAX_JSON_BYTES) {
            throw InvalidReleaseMetadataException("Метаданные $name превышают 1 MiB.")
        }
    }

    private fun string(json: JSONObject, name: String): String = json.opt(name) as? String
        ?: throw InvalidReleaseMetadataException("В метаданных отсутствует поле $name.")

    private fun boolean(json: JSONObject, name: String): Boolean = json.opt(name) as? Boolean
        ?: throw InvalidReleaseMetadataException("В метаданных отсутствует поле $name.")

    private fun integer(json: JSONObject, name: String): Long {
        val value = json.opt(name) as? Number
            ?: throw InvalidReleaseMetadataException("Некорректное числовое поле $name.")
        val result = value.toLong()
        if (value.toDouble() != result.toDouble()) {
            throw InvalidReleaseMetadataException("Поле $name должно быть целым числом.")
        }
        return result
    }

    private fun exactKeys(json: JSONObject, keys: Set<String>) {
        val actual = json.keys().asSequence().toSet()
        if (actual != keys) throw InvalidReleaseMetadataException("Набор полей манифеста не соответствует схеме.")
    }
}
