# Flutter's own rules are contributed by the Flutter Gradle plugin; these are the app's.
-keep class io.flutter.app.** { *; }
-keep class io.flutter.plugin.** { *; }
-keep class io.flutter.util.** { *; }
-keep class io.flutter.view.** { *; }
-keep class io.flutter.** { *; }
-keep class io.flutter.plugins.** { *; }

# cryptography uses reflection-free Dart, but its optional platform backends are looked up
# by name when present.
-keep class com.pravera.flutter_foreground_task.** { *; }

# ---------------------------------------------------------------------------
# Play Core
#
# Flutter's embedding references com.google.android.play.core.* from
# FlutterPlayStoreSplitApplication and PlayStoreDeferredComponentManager, whether or not the
# app uses deferred components. This app does not, so the Play Core library is not on the
# classpath - and R8 treats an unresolvable reference as a hard error in a release build
# rather than a warning, which fails the build outright:
#
#   ERROR: Missing classes detected while running R8
#   ERROR: R8: Missing class com.google.android.play.core.splitcompat.SplitCompatApplication
#
# The alternative is adding the Play Core dependency, which is a real dependency for a
# feature this app does not have. A keep rule suppresses the reference without pulling in
# the library, and the code paths are never reached because nothing defers a component.
#
# These are the exact rules R8 writes to missing_rules.txt for this failure.
-dontwarn com.google.android.play.core.**
-keep class io.flutter.embedding.engine.deferredcomponents.** { *; }
-keep class io.flutter.embedding.android.FlutterPlayStoreSplitApplication { *; }
