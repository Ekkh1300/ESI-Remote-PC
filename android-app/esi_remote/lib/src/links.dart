import 'package:flutter/services.dart';

/// The app's off-machine links (v1.1.0): where the PC half of this pair lives.
///
/// The phone runs the controls; the machine being controlled runs the Windows agent, and a
/// phone that controls a PC the visitor cannot install anything on is half a product. The
/// Settings screen's About panel carries the Windows-download row, which hands the URL to
/// the host (`MainActivity.open`) so it opens in the browser the phone already trusts -
/// no in-app web view to open, no second copy of anything suppurating inside TPM-grade
/// pinned links.
///
/// `latest` is GitHub's own alias: it resolves to the newest non-prerelease release, so the
/// row stays correct after every future release without touching this file again.
class EsiLinks {
  const EsiLinks._();

  /// The releases page for the Windows agent. Points at `latest`, which GitHub resolves to
  /// the newest non-prerelease release, notes and checksums included.
  static const String windowsDownloadUrl =
      'https://github.com/Ekkh1300/ESI-Remote-PC/releases/latest';

  /// The Android APK of the current release. `release/latest/download` serves the newest
  /// release's asset of that exact name without a page hop.
  static const String androidApkUrl =
      'https://github.com/Ekkh1300/ESI-Remote-PC/releases/latest/download/ESI-RemotePC-1.1.0.apk';

  /// Opens a URL in the phone's browser through the host's `esi_remote/links` channel.
  ///
  /// Returns null on success; a short failure note for anything the host could not do
  /// (no browser, the URL refused to open), which the caller can surface as a toast.
  static Future<String?> open(String url) async {
    final channel = const MethodChannel('esi_remote/links');
    try {
      await channel.invokeMethod<void>('open', <String, String>{'url': url});
      return null;
    } on PlatformException catch (e) {
      return e.message ?? 'The link could not be opened.';
    }
  }

  /// Opens the Windows agent's download page. The one button the Settings screen carries.
  static Future<String?> openWindowsDownload() => open(windowsDownloadUrl);
}
