import java.util.Properties

plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

// Release signing is driven by android/key.properties when present. Absent that file the
// release build falls back to the debug keys, which is only ever useful for local testing.
val keystoreProperties = Properties()
val keystorePropertiesFile = rootProject.file("key.properties")
val hasReleaseKeystore = keystorePropertiesFile.exists()
if (hasReleaseKeystore) {
    keystorePropertiesFile.inputStream().use { keystoreProperties.load(it) }
}

android {
    namespace = "com.esi.remotepc"
    // compileSdk 36 is required by mobile_scanner 7.x and fl_chart 1.2.x. It is *not* a
    // request to run on API 36; see targetSdk below.
    compileSdk = 36
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        applicationId = "com.esi.remotepc"
        // API 24 is the floor for flutter_secure_storage's EncryptedSharedPreferences and
        // for the modern TLS stack we rely on for SPKI pinning.
        minSdk = 24
        // Deliberately 36, not 37. Android 17 gates local-network access behind
        // ACCESS_LOCAL_NETWORK; a targetSdk of 36 receives an implicit grant, so the app can
        // reach the agent on the LAN without a permission that does not exist yet.
        targetSdk = 36
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    packaging {
        jniLibs {
            // The 32-bit ARM ABI is dropped, and not through `ndk.abiFilters`: that filter
            // does not reach prebuilt .so files inside third-party AARs, so ML Kit's copy of
            // the barcode model survived while Flutter's own engine and app libraries for
            // that ABI did not. aapt then advertised `armeabi-v7a` support from the leftover
            // file, and a 32-bit phone would have installed the package and died on launch
            // with an UnsatisfiedLinkError. Excluding the directory is the only thing that
            // makes the advertised ABI list true.
            excludes += setOf("**/armeabi-v7a/**")
        }
    }

    signingConfigs {
        if (hasReleaseKeystore) {
            create("release") {
                keyAlias = keystoreProperties["keyAlias"] as String
                keyPassword = keystoreProperties["keyPassword"] as String
                storeFile = keystoreProperties["storeFile"]?.let { rootProject.file(it) }
                storePassword = keystoreProperties["storePassword"] as String
            }
        }
    }

    buildTypes {
        release {
            signingConfig = if (hasReleaseKeystore) {
                signingConfigs.getByName("release")
            } else {
                signingConfigs.getByName("debug")
            }
            // A universal APK carries the Flutter engine and the Dart AOT library once per
            // ABI, and that is nearly all of it: 66.5 MB of a 69.9 MB package was lib/. The
            // build is invoked with
            //     flutter build apk --release --target-platform android-arm64,android-x64
            // which is what actually selects the ABIs; the packaging rule above removes the
            // rest. `x86_64` is emulator- and Chromebook-only, but without it the app cannot
            // be installed on an emulator at all, and an artefact nobody can install is not
            // one whose behaviour anybody can check. If a 32-bit ARM phone ever has to be
            // supported, drop the excludes rule and add the ABI to --target-platform.
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}
