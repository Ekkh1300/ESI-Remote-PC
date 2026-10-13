import 'dart:io';

import 'package:esi_remote/src/net/discovery.dart';
import 'package:esi_remote/src/state/discovery.dart';
import 'package:esi_remote/src/ui/router.dart';
import 'package:esi_remote/src/ui/shell.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

/// Loads the app's real fonts, which golden tests do not get for free.
///
/// Without this every glyph is drawn as a solid block by the test harness's fallback font and
/// every icon comes out a hollow box. The picture is then geometrically correct and reads as
/// nothing, which is barely better than no picture at all. MaterialIcons is fetched from the
/// Flutter SDK's own cached copy rather than the app, because the icons are half of what any
/// of these screens are showing.
Future<void> _loadFonts() async {
  Future<void> family(String name, List<String> files) async {
    final loader = FontLoader(name);
    for (final path in files) {
      final bytes = await File(path).readAsBytes();
      loader.addFont(Future<ByteData>.value(ByteData.view(bytes.buffer)));
    }
    await loader.load();
  }

  const weights = <int>[300, 400, 500, 600, 700];
  await family(
    'SpaceGrotesk',
    <String>[for (final w in weights) 'assets/fonts/SpaceGrotesk-$w.ttf'],
  );
  await family(
    'JetBrainsMono',
    <String>[for (final w in weights) 'assets/fonts/JetBrainsMono-$w.ttf'],
  );

  final sdk = Platform.environment['FLUTTER_ROOT'] ?? r'D:\dev\flutter';
  final icons = File(
      '$sdk/bin/cache/artifacts/material_fonts/materialicons-regular.otf');
  if (await icons.exists()) {
    await family('MaterialIcons', <String>[icons.path]);
  }
}

/// The app itself, at a phone's size, so the layout can be looked at rather than described.
///
/// Every other test in this suite measures one widget's geometry. That is the right way to
/// hold a property still, and it is also the wrong way to answer "does it look right", which
/// is a question only a picture answers. This renders the real [EsiRemoteApp] - real router,
/// real shell, real screens, real providers - at 1080x2400 logical-divided-by-3, which is
/// the exact size of the emulator this is developed against.
///
/// It is not a test of pixel equality. Nothing is compared against a stored image, so a
/// deliberate redesign does not fail here; what it produces is the PNG under
/// `test/goldens/`, which is what gets looked at when someone asks whether the rail puts
/// Control at the foot.
void main() {
  // The emulator's own numbers: a 1080x2400 panel at 3x density is 360x800 logical pixels.
  const logicalWidth = 360.0;
  const logicalHeight = 800.0;
  const density = 3.0;

  // Outside the test body, not inside it. `testWidgets` runs its body in a FakeAsync zone,
  // and reading a file is real I/O that the fake clock cannot drive: awaited there, it never
  // completes and every case times out at the ten-minute limit. `setUpAll` runs in the real
  // zone, so the fonts load once, before any fake clock exists.
  setUpAll(_loadFonts);

  Future<void> pumpApp(
    WidgetTester tester, {
    required String location,
  }) async {
    tester.view.physicalSize = Size(logicalWidth * density, logicalHeight * density);
    tester.view.devicePixelRatio = density;
    addTearDown(tester.view.reset);

    // The app keeps its paired-PC details in flutter_secure_storage, which has no platform
    // implementation under `flutter test`. Without this the channel throws on the very first
    // read and the screen never builds. Answering every call with null is what the app sees
    // when nothing has been stored yet, which is the state this is meant to photograph.
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );

    await tester.pumpWidget(
      ProviderScope(
        // The dashboard starts a LAN sweep the moment it builds, and the sweep opens sockets.
        // Socket I/O is real work that FakeAsync cannot complete, so those timers are still
        // armed when the body ends and the binding reports them as a failure - a property of
        // the harness rather than of the dashboard, and one no amount of pumping clears.
        //
        // `discoveryRunnerProvider` exists precisely so the network half can be swapped out
        // ("as a function so tests can replace the network work without a seam in the UI").
        // A sweep that finds nothing, immediately, is also what a screenshot wants: there is
        // no PC behind this.
        overrides: [
          discoveryRunnerProvider.overrideWithValue(
            ({void Function(DiscoveryMethod)? onProgress}) async =>
                DiscoveryResult(const [], methods: const <DiscoveryMethod>{}),
          ),
        ],
        child: EsiRemoteApp(),
      ),
    );
    await tester.pump(const Duration(milliseconds: 200));
    appRouter.go(location);
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pump(const Duration(milliseconds: 400));
  }

  testWidgets('dashboard renders with Control at the foot of the rail', (tester) async {
    await pumpApp(tester, location: '/dashboard');

    expect(tester.takeException(), isNull, reason: 'the dashboard screen threw');
    expect(find.byType(AppShell), findsOneWidget, reason: 'the shell did not build');

    // Measured as well as pictured, so a regression fails even without anyone looking.
    final control = tester.getRect(
      find.ancestor(of: find.text('Control'), matching: find.byType(InkWell)),
    );
    expect(control.bottom, greaterThanOrEqualTo(logicalHeight - 12),
        reason: 'Control sits ${logicalHeight - control.bottom}px above the foot of the rail');

    await expectLater(
      find.byType(AppShell),
      matchesGoldenFile('goldens/shell-dashboard.png'),
    );
  });

  testWidgets('the Control tab renders', (tester) async {
    await pumpApp(tester, location: '/media');

    expect(tester.takeException(), isNull, reason: 'the Control screen threw');
    expect(find.byType(AppShell), findsOneWidget);

    await expectLater(
      find.byType(AppShell),
      matchesGoldenFile('goldens/shell-control.png'),
    );
  });

  testWidgets('the files screen renders', (tester) async {
    await pumpApp(tester, location: '/files');

    expect(tester.takeException(), isNull, reason: 'the files screen threw');
    expect(find.byType(AppShell), findsOneWidget);

    await expectLater(
      find.byType(AppShell),
      matchesGoldenFile('goldens/shell-files.png'),
    );
  });

  testWidgets('the apps screen renders', (tester) async {
    await pumpApp(tester, location: '/apps');

    expect(tester.takeException(), isNull, reason: 'the apps screen threw');
    expect(find.byType(AppShell), findsOneWidget);

    await expectLater(
      find.byType(AppShell),
      matchesGoldenFile('goldens/shell-apps.png'),
    );
  });

  testWidgets('the power screen renders', (tester) async {
    await pumpApp(tester, location: '/power');

    expect(tester.takeException(), isNull, reason: 'the power screen threw');
    expect(find.byType(AppShell), findsOneWidget);

    await expectLater(
      find.byType(AppShell),
      matchesGoldenFile('goldens/shell-power.png'),
    );
  });

  testWidgets('the remote screen renders', (tester) async {
    await pumpApp(tester, location: '/remote');

    expect(tester.takeException(), isNull, reason: 'the remote screen threw');
    expect(find.byType(AppShell), findsOneWidget);

    await expectLater(
      find.byType(AppShell),
      matchesGoldenFile('goldens/shell-remote.png'),
    );
  });
}
