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
            // D52: the 32-bit ARM ABI was dropped wholesale, and that is why the APK would
            // not install on any 32-bit phone: `aapt` advertises only the ABIs whose native
            // libraries are present, so a phone with no armeabi-v7a match is told the package
            // is invalid.
            //
            // The original reason for dropping it was real: ML Kit's barcode scanner ships
            // `libbarhopper_v3.so` for armeabi-v7a while Flutter's engine and app libraries
            // for that ABI were being stripped, which left a half-present ABI - a phone would
            // install the package and then die on launch with an UnsatisfiedLinkError.
            //
            // The fix was one layer too broad. Only that one library is the problem; the rest
            // of Flutter's armeabi-v7a output is complete and usable. So the 32-bit ABI stays
            // and only the single ML Kit library is removed for it. The advertised ABI list is
            // now true, and it is true because the ABI works - not because it was hidden.
            excludes += setOf("**/armeabi-v7a/libbarhopper_v3.so")
        }
    }

    signingConfigs {
        if (hasReleaseKeystore) {
            create("release") {
                keyAlias = keystoreProperties["keyAlias"] as String
                keyPassword = keystoreProperties["keyPassword"] as String
                storeFile = keystoreProperties["storeFile"]?.let { rootProject.file(it) }
                storePassword = keystoreProperties["storePassword"] as String

                // D52: the release was signed with v2 only, and v1 verification came back false.
                // v1 is JAR signing, the scheme Android accepts below API 24, which looked for
                // a moment like a second cause of "app not installed as package appears to be
                // invalid" now that the 32-bit ABI was back.
                //
                // It is not one, and AGP is right to skip it. This package declares
                // minSdkVersion 24, so no device old enough to need v1 can install it anyway -
                // the floor is set by EncryptedSharedPreferences and the TLS stack, not by this
                // choice. Enabling v1 would add megabytes of signature files for a range that
                // cannot run the app.
                //
                // The real cause of the install failure was the missing 32-bit ABI, handled in
                // the packaging block above.
                enableV2Signing = true
                enableV3Signing = true
                enableV4Signing = false   // needs an accompanying .idsig; not for sideloading
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
            // ABI, and that is nearly all of it. The build is invoked as
            //     flutter build apk --release
            // which produces every ABI Flutter supports; the packaging rule above removes the
            // single ML Kit library that makes the 32-bit ABI unusable.
            //
            // x86_64 is emulator- and Chromebook-only, but without it the app cannot be
            // installed on an emulator at all, and an artefact nobody can install is not one
            // whose behaviour anybody can check.
            //
            // D52: the comment here used to tell a future reader to remove the packaging rule
            // above and add armeabi-v7a to --target-platform. Following it would have shipped
            // exactly the half-present ABI that rule exists to prevent - ML Kit's barcode
            // library would have come back for armeabi-v7a and a 32-bit phone would have
            // installed the package and then died on launch.
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
