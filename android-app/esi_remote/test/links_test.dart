import 'package:esi_remote/src/app_info.dart';
import 'package:esi_remote/src/links.dart';
import 'package:flutter_test/flutter_test.dart';

/// The two rows the Settings screen's About panel carries point here. GitHub's `latest`
/// alias resolves to the newest non-prerelease release, so the Windows row needs no edits
/// between releases - but the APK row names the asset of a SPECIFIC release, so it is
/// pinned: bumping the app's version without touching this URL would silently serve the
/// previous release's APK to anyone who pressed the row.
void main() {
  test('the Windows download row lands on the newest non-prerelease release', () {
    expect(
      EsiLinks.windowsDownloadUrl,
      'https://github.com/Ekkh1300/ESI-Remote-PC/releases/latest',
    );
  });

  test('the Android APK row names this release\'s asset', () {
    expect(
      EsiLinks.androidApkUrl,
      'https://github.com/Ekkh1300/ESI-Remote-PC/releases/latest/download/'
      'ESI-RemotePC-1.1.1.apk',
    );
  });

  // D93: the pairing flow used to carry its own copy of the version string, and
  // only the About panel got bumped to 1.1.0 - so the PC's pairing prompt kept
  // introducing the phone as "app 1.0.1". One constant, read everywhere, pinned
  // here so the next bump that forgets it fails this suite.
  test('every version string comes from the single esiAppVersion constant', () {
    expect(esiAppVersion, '1.1.1');
  });
}
