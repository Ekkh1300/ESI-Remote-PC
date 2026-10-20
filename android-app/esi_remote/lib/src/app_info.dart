/// The app's user-facing version, as one constant every screen and every wire
/// message that names the app reads from.
///
/// Before D93 this string existed in two places - the About panel and the pairing
/// flow - and only the About panel was bumped at 1.1.0, so the PC's pairing prompt
/// kept introducing the phone as "app 1.0.1" after the phone had been 1.1.0 for a
/// whole release. A version that lies in a trust prompt is worse than a missing
/// row: the human approving the pairing is told to weigh the phone's version.
///
/// Bumped together with pubspec.yaml's `version:` and the Windows side's
/// Directory.Build.props at release time; test/links_test.dart pins the value so
/// a release bump that forgets this file fails the suite instead of shipping.
const String esiAppVersion = '1.1.1';
