package com.barkfluff.BarkCloud.data.update

import android.content.Context
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.content.pm.Signature
import com.barkfluff.BarkCloud.BuildConfig
import java.io.File
import java.security.MessageDigest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

fun interface UpdateApkVerifier {
    suspend fun verify(file: File, metadata: AndroidReleaseMetadata)
}

class AndroidApkVerifier(private val context: Context) : UpdateApkVerifier {
    override suspend fun verify(file: File, metadata: AndroidReleaseMetadata) = withContext(Dispatchers.IO) {
        ApkIntegrity.verifyFile(file, metadata.apkSize, metadata.sha256)
        val packageManager = context.packageManager
        val archive = packageManager.getPackageArchiveInfo(
            file.absolutePath,
            PackageManager.GET_SIGNING_CERTIFICATES,
        ) ?: throw IllegalArgumentException("Загруженный файл не является Android APK.")
        if (archive.packageName != context.packageName) {
            throw IllegalArgumentException("APK предназначен для другого приложения.")
        }
        if (archive.longVersionCode != metadata.versionCode) {
            throw IllegalArgumentException("versionCode APK не совпадает с манифестом.")
        }
        if (archive.longVersionCode <= installedPackageInfo(packageManager).longVersionCode) {
            throw IllegalArgumentException("Версия APK должна быть новее установленной.")
        }
        if (archive.versionName != metadata.versionName) {
            throw IllegalArgumentException("versionName APK не совпадает с манифестом.")
        }
        val installed = installedPackageInfo(packageManager)
        val candidateSigners = signerSet(archive)
        val installedSigners = signerSet(installed)
        if (!ApkIntegrity.signingCompatible(candidateSigners, installedSigners)) {
            val message = if (BuildConfig.DEBUG) {
                "Подпись APK не совпадает с установленной debug-сборкой. Используйте APK с тем же сертификатом."
            } else {
                "Подпись APK не совместима с установленной версией BarkCloud."
            }
            throw IllegalArgumentException(message)
        }
    }

    @Suppress("DEPRECATION")
    private fun installedPackageInfo(packageManager: PackageManager): PackageInfo =
        packageManager.getPackageInfo(context.packageName, PackageManager.GET_SIGNING_CERTIFICATES)

    private fun signerSet(info: PackageInfo): ApkSignerSet {
        val signingInfo = info.signingInfo
            ?: throw IllegalArgumentException("Не удалось прочитать сертификаты подписи APK.")
        val current = signingInfo.apkContentsSigners?.map(::certificateHash)?.toSet().orEmpty()
        val history = signingInfo.signingCertificateHistory?.map(::certificateHash)?.toSet().orEmpty()
        if (current.isEmpty()) throw IllegalArgumentException("APK не содержит сертификата подписи.")
        return ApkSignerSet(current, history, signingInfo.hasMultipleSigners())
    }

    private fun certificateHash(signature: Signature): String = MessageDigest.getInstance("SHA-256")
        .digest(signature.toByteArray())
        .joinToString("") { "%02x".format(it) }
}
