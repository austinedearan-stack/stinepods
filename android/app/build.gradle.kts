import java.util.Properties

plugins {
    id("com.android.application")
}

// Release signing key lives outside the repo (C:\dev\keys); without it, release builds fall back to debug signing.
val releaseKey = Properties().apply {
    val f = file("C:/dev/keys/stinepods-release.properties")
    if (f.exists()) f.inputStream().use(::load)
}

android {
    namespace = "com.stinepods.app"
    compileSdk = 37

    defaultConfig {
        // Provisional id (docs/DECISIONS.md D6); revisit before any Play Store release.
        applicationId = "com.stinepods.app"
        minSdk = 29
        targetSdk = 36
        versionCode = 2
        versionName = "0.2.0"
    }

    buildFeatures {
        buildConfig = true
    }

    flavorDimensions += "distribution"
    productFlavors {
        // Sideloaded APK (the default): no notification listener, so Play Protect's fraud protection, which blocks
        // sideloaded apps declaring one in Kenya and other countries, has nothing to flag.
        create("lite") {
            dimension = "distribution"
            buildConfigField("boolean", "FULL", "false")
        }
        // Store build: adds media-session access for per-app rules. For Play / adb installs.
        create("full") {
            dimension = "distribution"
            buildConfigField("boolean", "FULL", "true")
        }
    }

    signingConfigs {
        if (releaseKey.isNotEmpty()) create("release") {
            storeFile = file(releaseKey.getProperty("storeFile"))
            storePassword = releaseKey.getProperty("storePassword")
            keyAlias = releaseKey.getProperty("keyAlias")
            keyPassword = releaseKey.getProperty("keyPassword")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    testOptions {
        unitTests.all { it.systemProperty("stinepods.root", rootProject.projectDir.parentFile.absolutePath) }
    }
}

dependencies {
    // Lifts the hidden-API filter for android.bluetooth (see Compat.kt). Apache-2.0.
    implementation("org.lsposed.hiddenapibypass:hiddenapibypass:6.2")
    testImplementation("junit:junit:4.13.2")
    // Android ships org.json at runtime; plain JVM unit tests need a real implementation.
    testImplementation("org.json:json:20260814")
}
