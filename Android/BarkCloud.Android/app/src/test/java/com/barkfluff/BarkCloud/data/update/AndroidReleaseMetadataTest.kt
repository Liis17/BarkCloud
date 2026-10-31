package com.barkfluff.BarkCloud.data.update

import java.time.LocalDate
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AndroidReleaseMetadataTest {
    @Test
    fun versionCodeUsesTagAndKeepsLegacyVersionNamesIrrelevant() {
        assertEquals(1_000_004L, AndroidReleaseParser.versionCode("v1.0.4"))
        assertEquals(2_017_999L, AndroidReleaseParser.versionCode("v2.17.999"))
        assertTrue(AndroidUpdatePolicy.shouldPrompt(
            metadata("v1.0.4"),
            installedVersionCode = 2,
            nowDay = LocalDate.of(2026, 10, 31),
            promptedDay = null,
        ))
    }

    @Test
    fun rejectsInvalidSemverComponentsAndAndroidOverflow() {
        listOf("v1.1000.0", "v1.0.1000", "v2148.0.0", "v01.0.0", "v1.0.1-rc.1").forEach { tag ->
            expectThrows<InvalidReleaseMetadataException> { AndroidReleaseParser.versionCode(tag) }
        }
    }

    @Test
    fun parsesStableReleaseAndStrictManifest() {
        val manifest = manifest()
        val latest = AndroidReleaseParser.parseLatest(
            apiRelease(
                tag = "v1.0.5",
                notes = "First line\n* second line",
                manifestSize = manifest.size.toLong(),
            ),
        )
        val parsed = AndroidReleaseParser.parseManifest(manifest, latest)

        assertEquals("v1.0.5", parsed.tag)
        assertEquals("1.0.5", parsed.versionName)
        assertEquals(1_000_005L, parsed.versionCode)
        assertEquals("BarkCloud.Android-v1.0.5.apk", parsed.apkFileName)
        assertEquals("ab".repeat(32), parsed.sha256)
        assertEquals("First line\n* second line", parsed.notes)
        assertEquals(
            "https://github.com/Liis17/BarkCloud/releases/download/v1.0.5/BarkCloud.Android-v1.0.5.apk",
            parsed.downloadUrl,
        )
    }

    @Test
    fun reportsLegacyReleaseWithoutUpdaterManifest() {
        val latest = AndroidReleaseParser.parseLatest(apiRelease(manifestSize = null))
        val error = expectThrows<MissingUpdaterMetadataException> {
            AndroidReleaseParser.parseManifest(ByteArray(0), latest)
        }
        assertEquals("v1.0.5", error.latestTag)
    }

    @Test
    fun cachedMetadataCannotSupplyAnArbitraryAssetPathOrInvalidVersion() {
        val parsed = AndroidReleaseParser.parseManifest(
            manifest(),
            AndroidReleaseParser.parseLatest(apiRelease(manifestSize = manifest().size.toLong())),
        )
        val arbitraryName = JSONObject(parsed.toJson()).put("apkFileName", "../../other.apk").toString()
        val wrongVersion = JSONObject(parsed.toJson()).put("versionCode", 2).toString()

        expectThrows<InvalidReleaseMetadataException> { AndroidReleaseMetadata.fromJson(arbitraryName) }
        expectThrows<InvalidReleaseMetadataException> { AndroidReleaseMetadata.fromJson(wrongVersion) }
    }

    @Test
    fun rejectsManifestTagNameCodeSizeAssetAndDigestMismatches() {
        val valid = manifest()
        val latest = AndroidReleaseParser.parseLatest(apiRelease(manifestSize = valid.size.toLong()))
        val wrongTag = JSONObject(valid.toString(Charsets.UTF_8)).put("tag", "v1.0.4").toString().toByteArray()
        val wrongName = JSONObject(valid.toString(Charsets.UTF_8)).put("versionName", "1.0.4").toString().toByteArray()
        val wrongCode = JSONObject(valid.toString(Charsets.UTF_8)).put("versionCode", 2).toString().toByteArray()
        val wrongSize = JSONObject(valid.toString(Charsets.UTF_8)).apply {
            getJSONObject("apk").put("size", 999)
        }.toString().toByteArray()
        val wrongDigest = JSONObject(valid.toString(Charsets.UTF_8)).apply {
            getJSONObject("apk").put("sha256", "xyz")
        }.toString().toByteArray()
        listOf(wrongTag, wrongName, wrongCode, wrongSize, wrongDigest).forEach { bytes ->
            expectThrows<InvalidReleaseMetadataException> { AndroidReleaseParser.parseManifest(bytes, latest) }
        }

        val wrongApiSize = AndroidReleaseParser.parseLatest(apiRelease(manifestSize = valid.size + 1L))
        expectThrows<InvalidReleaseMetadataException> { AndroidReleaseParser.parseManifest(valid, wrongApiSize) }
    }

    @Test
    fun rejectsDraftPrereleaseAndOversizedJson() {
        expectThrows<InvalidReleaseMetadataException> {
            AndroidReleaseParser.parseLatest(apiRelease(manifestSize = 10).replace("\"draft\":false", "\"draft\":true"))
        }
        expectThrows<InvalidReleaseMetadataException> {
            AndroidReleaseParser.parseLatest(apiRelease(manifestSize = 10).replace("\"prerelease\":false", "\"prerelease\":true"))
        }
        expectThrows<InvalidReleaseMetadataException> {
            AndroidReleaseParser.parseLatest(" ".repeat(AndroidReleaseParser.MAX_JSON_BYTES + 1))
        }
        expectThrows<InvalidReleaseMetadataException> {
            AndroidReleaseParser.parseManifest(ByteArray(AndroidReleaseParser.MAX_JSON_BYTES + 1),
                AndroidReleaseParser.parseLatest(apiRelease(manifestSize = 10)))
        }
    }

    @Test
    fun validatesRateLimitAndClockRollbackBoundaries() {
        assertFalse(AndroidUpdatePolicy.isDue(900, 1_000, 100))
        assertFalse(AndroidUpdatePolicy.isDue(1_099, 1_000, 100))
        assertTrue(AndroidUpdatePolicy.isDue(1_100, 1_000, 100))
        assertTrue(AndroidUpdatePolicy.isRateLimitActive(1_099, 1_100))
        assertFalse(AndroidUpdatePolicy.isRateLimitActive(1_100, 1_100))
    }

    private fun metadata(tag: String) = AndroidReleaseMetadata(
        tag = tag,
        versionName = tag.removePrefix("v"),
        versionCode = AndroidReleaseParser.versionCode(tag),
        apkFileName = "BarkCloud.Android-$tag.apk",
        apkSize = 128,
        sha256 = "ab".repeat(32),
        notes = "",
    )

    private fun manifest(): ByteArray = JSONObject()
        .put("schemaVersion", 1)
        .put("tag", "v1.0.5")
        .put("versionName", "1.0.5")
        .put("versionCode", 1_000_005)
        .put(
            "apk",
            JSONObject()
                .put("fileName", "BarkCloud.Android-v1.0.5.apk")
                .put("size", 128)
                .put("sha256", "ab".repeat(32)),
        )
        .toString()
        .toByteArray()

    private fun apiRelease(
        tag: String = "v1.0.5",
        notes: String = "",
        manifestSize: Long?,
    ): String {
        val assets = JSONArray()
            .put(JSONObject().put("name", "BarkCloud.Android-$tag.apk").put("size", 128))
        if (manifestSize != null) {
            assets.put(JSONObject().put("name", AndroidReleaseParser.MANIFEST_NAME).put("size", manifestSize))
        }
        return JSONObject()
            .put("tag_name", tag)
            .put("draft", false)
            .put("prerelease", false)
            .put("body", notes)
            .put("assets", assets)
            .toString()
    }
}

internal inline fun <reified T : Throwable> expectThrows(block: () -> Unit): T {
    try {
        block()
    } catch (error: Throwable) {
        if (error is T) return error
        throw AssertionError("Expected ${T::class.java.simpleName}, got ${error::class.java.simpleName}.", error)
    }
    throw AssertionError("Expected ${T::class.java.simpleName}.")
}
