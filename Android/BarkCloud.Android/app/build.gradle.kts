import com.google.protobuf.gradle.id

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.protobuf)
}

android {
    namespace = "com.barkfluff.BarkCloud"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.barkfluff.BarkCloud"
        minSdk = 30
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"

        buildConfigField(
            "String",
            "IDENTITY_API_ADDRESS",
            "\"https://cloud.barkfluff.com:7020\""
        )
        buildConfigField(
            "String",
            "USERS_API_ADDRESS",
            "\"https://cloud.barkfluff.com:7021\""
        )
        buildConfigField(
            "String",
            "FILES_API_ADDRESS",
            "\"https://cloud.barkfluff.com:7025\""
        )
        buildConfigField(
            "String",
            "FILES_WEB_BASE",
            "\"https://cloud.barkfluff.com:7025/web\""
        )
    }

    buildTypes {
        release {
            isMinifyEnabled = false
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
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    debugImplementation(libs.androidx.compose.ui.tooling)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.extended)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.security.crypto)

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

    testImplementation(libs.junit)
    testImplementation(libs.mockk)
    testImplementation(libs.turbine)
    testImplementation(libs.kotlinx.coroutines.test)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(libs.androidx.junit)
}
