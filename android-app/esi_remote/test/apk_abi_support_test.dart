import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// D52: the APK would not install on 32-bit phones.
///
/// `app not installed as package appears to be invalid` is what a package installer says
/// when nothing in the APK matches the device. The usual cause is an ABI list with nothing
/// the phone can run.
///
/// And this APK had that. `build.gradle.kts` excluded the whole of `armeabi-v7a`, so the
/// shipped package advertised `native-code: 'arm64-v8a' 'x86_64'` and any 32-bit ARM phone -
/// still a large share of devices in the markets this would ship to - was refused.
///
/// The exclusion was not arbitrary. It existed because ML Kit's barcode scanner ships
/// `libbarhopper_v3.so` for armeabi-v7a while Flutter's own libraries for that ABI were being
/// stripped, which produced a half-present ABI: the package installed and then died on
/// launch with an UnsatisfiedLinkError. That is a real failure, and it was fixed one layer too
/// broad. Only that single library is the problem, so only that single library is excluded now
/// and the ABI stays.
///
/// A second, separate installer rejection was fixed alongside it: the release was signed with
/// v2 only. v1 is JAR signing and the only scheme Android accepts below API 24 - which is
/// precisely the range this APK now has to install on. That produces the same error message
/// as a missing ABI, which is what makes the two so hard to tell apart in the field.
void main() {
  final gradle = File('android/app/build.gradle.kts');

  String source() {
    expect(gradle.existsSync(), isTrue, reason: 'app/build.gradle.kts moved');
    return gradle.readAsStringSync();
  }

  /// The exclusion globs, read from the `excludes` lines alone.
  ///
  /// Filtering whole-file comments is fragile here and keeps being fragile: `**/armeabi-v7a/**`
  /// is made of the same characters a comment marker is made of, so any stripper that is not
  /// exactly right deletes the glob under test. The assertion is about one line of Gradle, so
  /// reading that line directly is both simpler and more honest about what it checks.
  String excludes() =>
      source()
          .split('\n')
          .where((line) => line.contains('excludes'))
          .join('\n');

  group('the 32-bit ABI is supported', () {
    test('the whole ABI directory is not excluded', () {
      final text = excludes();

      // The defect verbatim. `**/armeabi-v7a/**` removes Flutter's engine and app libraries
      // for that ABI, so aapt stops advertising it and 32-bit phones cannot install.
      expect(text.contains('**/armeabi-v7a/**'), isFalse,
          reason: 'the entire 32-bit ABI is excluded again, so 32-bit phones are refused '
              'with "package appears to be invalid" (D52)');
    });

    test('only the ML Kit library is excluded, and by name', () {
      final text = excludes();

      // The narrow fix. `libbarhopper_v3.so` is the one library that makes armeabi-v7a
      // unusable, because Flutter's own output for that ABI is complete.
      expect(text.contains('**/armeabi-v7a/libbarhopper_v3.so'), isTrue,
          reason: 'the exclusion must name the one library that cannot work on 32-bit, or the '
              'ABI goes back to being half-present');
      expect(text.contains('libbarhopper_v3.so'), isTrue,
          reason: 'naming the library is what makes the rule reviewable - an unnamed '
              'exclusion will be widened again by the next person who hits it');
    });

    test('nothing else is excluded by wildcard under that ABI', () {
      final text = excludes();

      // A future `**/armeabi-v7a/libsomething.so**` would be a new, unreviewed removal. The
      // rule is one library, and the test says so.
      final wildcards = RegExp(r'\*\*/armeabi-v7a/\*\*');
      expect(wildcards.hasMatch(text), isFalse,
          reason: 'a wildcard under armeabi-v7a would silently drop more 32-bit libraries');
    });
  });

  group('the package is signed in a way its supported devices accept', () {
    test('v2 and v3 are on', () {
      final text = source();

      expect(text.contains('enableV2Signing = true'), isTrue);
      expect(text.contains('enableV3Signing = true'), isTrue,
          reason: 'v3 carries key rotation and costs nothing on devices that ignore it');
    });

    test('v1 is deliberately off, not accidentally off', () {
      final text = source();

      // An earlier version of this fix turned v1 on, believing it was a second cause of the
      // install failure. It is not: this package declares minSdkVersion 24, and no device
      // old enough to require v1 can install it in the first place. v1 would add megabytes of
      // signature files for a range that cannot run the app.
      //
      // What matters is that the choice is written down. An unsigned-by-accident v2-only
      // release and a deliberately v2-only release produce the same bytes and the same
      // apksigner output, and only one of them is a decision.
      expect(text.contains('enableV1Signing'), isFalse,
          reason: 'v1 is skipped deliberately - minSdk 24 makes it dead weight. Re-enabling '
              'it would add megabytes of signature files for devices that cannot install '
              'this package anyway (D52)');
      expect(text.contains('minSdkVersion 24'), isTrue,
          reason: 'the reason v1 is unnecessary should stay next to the line that omits it');
    });

    test('v4 is off, because it needs a sidecar file that sideloading does not carry', () {
      final text = source();

      expect(text.contains('enableV4Signing = false'), isTrue,
          reason: 'v4 requires an accompanying .idsig; enabling it without shipping that '
              'changes nothing but invites the question later');
    });
  });

  group('minSdk is a decision, not an accident', () {
    test('it is still 24', () {
      final text = source();

      // Android 7.0. Lowering it would mean weakening whatever needed it - the note on the
      // line says EncryptedSharedPreferences and the TLS stack for SPKI pinning - and that is
      // a decision to make on purpose, not while chasing an install error.
      expect(text.contains('minSdk = 24'), isTrue,
          reason: 'if minSdk moves, the reason in the comment above it has to move with it');
      expect(text.contains('EncryptedSharedPreferences'), isTrue,
          reason: 'the floor exists for a stated reason; the reason should stay next to it');
    });
  });
}