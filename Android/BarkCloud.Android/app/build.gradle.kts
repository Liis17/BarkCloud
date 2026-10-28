import com.google.protobuf.gradle.id
import com.google.protobuf.gradle.proto
import java.io.FileInputStream
import java.security.KeyStore
import java.security.MessageDigest
import java.util.Base64

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    id("kotlin-kapt")
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.protobuf)
}

val releaseSigningVariableNames = listOf(
    "BARKCLOUD_ANDROID_KEYSTORE_BASE64",
    "BARKCLOUD_ANDROID_KEYSTORE_PASSWORD_BASE64",
    "BARKCLOUD_ANDROID_KEY_ALIAS_BASE64",
    "BARKCLOUD_ANDROID_KEY_PASSWORD_BASE64",
)
val releaseSigningValues = releaseSigningVariableNames.associateWith {
    System.getenv(it)?.takeIf(String::isNotBlank)
}
val missingReleaseSigningValues = releaseSigningValues.filterValues { it == null }.keys
val releaseSigningConfigured = missingReleaseSigningValues.isEmpty()
if (!releaseSigningConfigured && releaseSigningValues.values.any { it != null }) {
    throw GradleException(
        "Android release signing requires all Base64 environment variables: " +
            missingReleaseSigningValues.joinToString(", ")
    )
}

fun decodeReleaseSigningValue(name: String): ByteArray {
    val encoded = releaseSigningValues[name]
        ?: throw GradleException("Missing Android signing environment variable: $name")
    return try {
        Base64.getDecoder().decode(encoded.filterNot { it.isWhitespace() })
    } catch (error: IllegalArgumentException) {
        throw GradleException("$name must contain valid Base64.", error)
    }
}

val releaseKeystoreBytes = if (releaseSigningConfigured) {
    decodeReleaseSigningValue("BARKCLOUD_ANDROID_KEYSTORE_BASE64")
} else {
    null
}
val releaseKeystoreFile = releaseKeystoreBytes?.let { bytes ->
    layout.buildDirectory.file("signing/android-release.keystore").get().asFile.apply {
        parentFile.mkdirs()
        writeBytes(bytes)
    }
}
val releaseKeystoreType = releaseKeystoreBytes?.let { bytes ->
    val jksMagic = byteArrayOf(0xFE.toByte(), 0xED.toByte(), 0xFE.toByte(), 0xED.toByte())
    if (bytes.take(4).toByteArray().contentEquals(jksMagic)) "JKS" else "PKCS12"
}
val releaseStorePassword = if (releaseSigningConfigured) {
    decodeReleaseSigningValue("BARKCLOUD_ANDROID_KEYSTORE_PASSWORD_BASE64").toString(Charsets.UTF_8)
} else {
    null
}
val releaseKeyAlias = if (releaseSigningConfigured) {
    decodeReleaseSigningValue("BARKCLOUD_ANDROID_KEY_ALIAS_BASE64").toString(Charsets.UTF_8)
} else {
    null
}
val releaseKeyPassword = if (releaseSigningConfigured) {
    decodeReleaseSigningValue("BARKCLOUD_ANDROID_KEY_PASSWORD_BASE64").toString(Charsets.UTF_8)
} else {
    null
}

android {
    namespace = "com.barkfluff.BarkCloud"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.barkfluff.BarkCloud"
        minSdk = 30
        targetSdk = 36
        versionCode = 2
        versionName = "1.1"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"

        buildConfigField(
            "String",
            "IDENTITY_API_ADDRESS",
            "\"https://cloud.barkfluff.com:8000\""
        )
        buildConfigField(
            "String",
            "USERS_API_ADDRESS",
            "\"https://cloud.barkfluff.com:8001\""
        )
        buildConfigField(
            "String",
            "FILES_API_ADDRESS",
            "\"https://cloud.barkfluff.com:8005\""
        )
        buildConfigField(
            "String",
            "FILES_WEB_BASE",
            "\"https://cloud.barkfluff.com:8005/web\""
        )
        // Data plane Upload 2.0: nginx маршрутизирует /file-upload/ с 443 напрямую в Files.
        buildConfigField(
            "String",
            "FILES_UPLOAD_BASE",
            "\"https://cloud.barkfluff.com\""
        )
    }

    sourceSets.getByName("main") {
        proto { srcDir("src/main/proto-local") }
    }

    signingConfigs {
        if (releaseSigningConfigured) {
            create("release") {
                storeFile = requireNotNull(releaseKeystoreFile)
                storeType = requireNotNull(releaseKeystoreType)
                storePassword = requireNotNull(releaseStorePassword)
                keyAlias = requireNotNull(releaseKeyAlias)
                keyPassword = requireNotNull(releaseKeyPassword)
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (releaseSigningConfigured) {
                signingConfig = signingConfigs.getByName("release")
            }
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }

    kotlinOptions {
        jvmTarget = "11"
    }
}

gradle.taskGraph.whenReady {
    val packagesReleaseApk = allTasks.any { task ->
        task.name.contains("Release", ignoreCase = true) &&
            (task.name.startsWith("assemble") ||
                task.name.startsWith("package") ||
                task.name.startsWith("bundle"))
    }
    if (packagesReleaseApk && !releaseSigningConfigured) {
        throw GradleException(
            "Release APK signing is required. Set all Base64 environment variables: " +
                releaseSigningVariableNames.joinToString(", ")
        )
    }
}

tasks.register("printReleaseCertificateFingerprint") {
    group = "build"
    description = "Prints the SHA-256 signing certificate fingerprint for server asset links."
    doLast {
        val keystoreFile = releaseKeystoreFile
            ?: throw GradleException("Set all Android release signing environment variables first.")
        val storeType = releaseKeystoreType
            ?: throw GradleException("Android release keystore type could not be determined.")
        val storePassword = releaseStorePassword
            ?: throw GradleException("Android release keystore password is missing.")
        val keyAlias = releaseKeyAlias
            ?: throw GradleException("Android release key alias is missing.")
        val keystore = KeyStore.getInstance(storeType)
        FileInputStream(keystoreFile).use { keystore.load(it, storePassword.toCharArray()) }
        val certificate = keystore.getCertificate(keyAlias)
            ?: throw GradleException("No certificate found for the configured Android key alias.")
        val fingerprint = MessageDigest.getInstance("SHA-256")
            .digest(certificate.encoded)
            .joinToString(":") { (it.toInt() and 0xff).toString(16).uppercase().padStart(2, '0') }
        println("BARKCLOUD_ANDROID_CERT_SHA256_FINGERPRINT=$fingerprint")
    }
}

// Копируем общие proto-определения из Shared/BarkCloud.Proto в дефолтное место
// (app/src/main/proto), которое protobuf-gradle-plugin сканирует автоматически.
val syncSharedProto = tasks.register<Sync>("syncSharedProto") {
    from(project.file("../../../Shared/BarkCloud.Proto")) {
        include("**/*.proto")
    }
    into(project.file("src/main/proto"))
}

protobuf {
    protoc {
        artifact = "com.google.protobuf:protoc:${libs.versions.protobufJava.get()}"
    }
    plugins {
        id("grpc") {
            artifact = "io.grpc:protoc-gen-grpc-java:${libs.versions.grpc.get()}"
        }
        id("grpckt") {
            artifact = "io.grpc:protoc-gen-grpc-kotlin:${libs.versions.grpcKotlin.get()}:jdk8@jar"
        }
    }
    generateProtoTasks {
        all().forEach { task ->
            task.dependsOn(syncSharedProto)
            task.builtins {
                id("java") { option("lite") }
                id("kotlin") { option("lite") }
            }
            task.plugins {
                id("grpc") { option("lite") }
                id("grpckt") { option("lite") }
            }
        }
    }
}

tasks.matching {
    it.name.startsWith("extract") && it.name.endsWith("Proto") ||
        it.name.startsWith("extractInclude")
}.configureEach {
    dependsOn(syncSharedProto)
}

// Удерживаем версию kotlin-stdlib совместимой с компилятором (libs.versions.kotlin):
// некоторые зависимости (например Coil 3.4.x) приносят stdlib 2.3.x, который компилятор
// 2.1.x читать не умеет.
configurations.configureEach {
    resolutionStrategy.eachDependency {
        if (requested.group == "org.jetbrains.kotlin" &&
            requested.name.startsWith("kotlin-stdlib")
        ) {
            useVersion(libs.versions.kotlin.get())
        }
        // Material 3 Expressive API публичен только в ветке 1.4.0-alpha (в стабильной 1.4.0
        // он internal). Стабильная версия «выше» альфы, поэтому форсим явно.
        if (requested.group == "androidx.compose.material3" &&
            (requested.name == "material3" || requested.name == "material3-android")
        ) {
            useVersion(libs.versions.material3Expressive.get())
        }
    }
}

dependencies {
    val composeBom = platform(libs.androidx.compose.bom)
    implementation(composeBom)
    androidTestImplementation(composeBom)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.core.splashscreen)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    debugImplementation(libs.androidx.compose.ui.tooling)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.extended)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.process)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.security.crypto)
    implementation(libs.androidx.biometric)
    implementation(libs.androidx.credentials)
    implementation(libs.androidx.credentials.play.services.auth)

    implementation(libs.protobuf.javalite)
    implementation(libs.protobuf.kotlin.lite)
    implementation(libs.grpc.okhttp)
    implementation(libs.grpc.protobuf.lite)
    implementation(libs.grpc.stub)
    implementation(libs.grpc.kotlin.stub)

    implementation(libs.kotlinx.coroutines.android)

    implementation(libs.coil.compose)
    implementation(libs.coil.video)
    implementation(libs.coil.network.okhttp)
    implementation(libs.okhttp)

    implementation(libs.androidx.graphics.shapes)
    implementation(libs.androidx.work.runtime.ktx)
    implementation(libs.androidx.room.runtime)
    implementation(libs.androidx.room.ktx)
    kapt(libs.androidx.room.compiler)

    testImplementation(libs.junit)
    testImplementation(libs.mockk)
    testImplementation(libs.turbine)
    testImplementation(libs.kotlinx.coroutines.test)
    // Реальный org.json в JVM-тестах (в android.jar только кидающий стабы).
    testImplementation("org.json:json:20240303")
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
    testImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
    testImplementation("com.squareup.okhttp3:okhttp-tls:4.12.0")
}
