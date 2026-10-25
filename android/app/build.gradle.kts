plugins {
    id("com.android.application")
}

android {
    namespace = "com.stinepods.app"
    compileSdk = 37

    defaultConfig {
        // Provisional id (docs/DECISIONS.md D6); revisit before any Play Store release.
        applicationId = "com.stinepods.app"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
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
