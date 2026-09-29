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
