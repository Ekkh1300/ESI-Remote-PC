// Google Maven (`dl.google.com`) is unreachable on some networks, including the one this
// project was built on, where every path under it returns 404. The mirrors below serve
// byte-identical artifacts and are listed *after* `google()`, so on a normal network
// nothing changes and the canonical repository still wins.
//
// The `content` filters keep the mirrors to the groups that actually live on Google Maven
// (the Android toolchain, AndroidX, and Google's own artifacts such as ML Kit), so
// nothing else is fetched from a third party.
//
// Remove the `maven { … }` blocks if your environment can reach `dl.google.com` directly;
// the build works without them there. See "Known limitations" in the README.
val googleMavenMirrors = listOf(
    "https://maven.aliyun.com/repository/google",
    "https://repo.huaweicloud.com/repository/maven",
)

fun RepositoryHandler.googleMavenMirrors() {
    for (mirror in googleMavenMirrors) {
        maven {
            url = uri(mirror)
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("androidx.*")
                includeGroupByRegex("com\\.google\\..*")
            }
        }
    }
}

allprojects {
    repositories {
        google()
        mavenCentral()
        googleMavenMirrors()
    }
}

// Some plugins (`android_file_picker`, for one) still declare their own `buildscript`
// block with a hard-coded AGP version. That block resolves against its own repository
// list, which `allprojects` does not reach, so the mirrors are added there too. They are
// additive, so `google()` still wins on a network that can reach it.
subprojects {
    buildscript {
        repositories {
            googleMavenMirrors()
        }
    }
}

val newBuildDir: Directory =
    rootProject.layout.buildDirectory
        .dir("../../build")
        .get()
rootProject.layout.buildDirectory.value(newBuildDir)
subprojects {
    val newSubprojectBuildDir: Directory = newBuildDir.dir(project.name)
    project.layout.buildDirectory.value(newSubprojectBuildDir)
}
subprojects {
    project.evaluationDependsOn(":app")
}

tasks.register<Delete>("clean") {
    delete(rootProject.layout.buildDirectory)
}
