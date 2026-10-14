import 'dart:io';

import 'package:esi_remote/src/ui/screens/remote_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

/// Full screen for the screen share, and the two things that would quietly break it.
///
/// The feature itself is one route and one button, so what is worth measuring is everything
/// around it: that it can be left again, and that the one row of controls drawn *on top of*
/// the remote desktop does not turn part of the PC into a region of the phone.
///
/// The second one is not hypothetical. Every pixel of this view is a pixel of somebody else's
/// desktop, so a readout sitting over its top-left corner - which is exactly where a status
/// line belongs - would silently swallow the first click of every session. It is skipped
/// during hit testing; this asserts that it is, and that the desktop underneath still answers.
void main() {
  /// The app at a phone's size, with one button that opens the full-screen view.
  ///
  /// Real providers, no overrides: nothing here is paired, so the socket and the REST client
  /// are both null, the session status resolves to `unknownRemoteStatus` immediately, and no
  /// network work or timer is ever started. Only `flutter_secure_storage` needs an answer,
  /// because the machine list reads it the moment it is instantiated - and answering null is
  /// what the phone sees when nothing has been stored, which is the state under test.
  Future<void> pumpApp(WidgetTester tester, {Size? size}) async {
    if (size != null) {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 3;
      addTearDown(tester.view.reset);
    }

    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );

    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          home: Builder(
            builder: (context) => Scaffold(
              body: Center(
                child: TextButton(
                  onPressed: () => Navigator.of(context).push<void>(
                    MaterialPageRoute<void>(
                      builder: (_) => EsiFullscreenStream(
                        surface: ScreenSurface(),
                        inputEnabled: false,
                      ),
                    ),
                  ),
                  child: const Text('open full screen'),
                ),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> openFullScreen(WidgetTester tester) async {
    await tester.tap(find.text('open full screen'));
    await tester.pumpAndSettle();
    expect(find.byType(EsiFullscreenStream), findsOneWidget, reason: 'the view did not open');
    expect(tester.takeException(), isNull, reason: 'the view threw once open');
  }

  testWidgets('the exit control closes the view', (tester) async {
    await pumpApp(tester);
    await openFullScreen(tester);

    expect(find.byTooltip('Leave full screen'), findsOneWidget,
        reason: 'full screen with no way out is a trap: the system back gesture also works, '
            'but it is invisible and nobody looks for a gesture they have not been shown');

    await tester.tap(find.byTooltip('Leave full screen'));
    await tester.pumpAndSettle();

    expect(find.byType(EsiFullscreenStream), findsNothing,
        reason: 'the exit control did not leave the mode');
    expect(find.text('open full screen'), findsOneWidget, reason: 'the screen behind is gone');
    expect(tester.takeException(), isNull);
  });

  testWidgets('the bar says why there is no picture', (tester) async {
    await pumpApp(tester);
    await openFullScreen(tester);

    // No session helper is connected on this phone, so the state is the unknown one.
    //
    // Without this the view is a black rectangle over the whole display with no difference
    // between "the PC has not started sending" and "the phone cannot reach the PC at all" -
    // and full screen is precisely the mode where someone will sit waiting, because the
    // whole screen being taken up is what an app looks like when it is working.
    expect(
      find.text('Session helper is not connected'),
      findsOneWidget,
      reason: 'the two states where the picture is a lie have to be named in the corner',
    );

    // The exit control shares the bar, so both being present at once is the layout.
    expect(find.byTooltip('Leave full screen'), findsOneWidget);
  });

  testWidgets('the corner readout does not take taps meant for the desktop', (tester) async {
    // Landscape, 800x360, and a 16:9 desktop: the picture is 640x360 centred, so it runs
    // from the top edge down - which is the one arrangement where the status bar sits over
    // real desktop rather than over the letterbox beside it. On an upright phone the bar
    // lands in the black gutter and the test would pass without proving anything.
    await pumpApp(tester, size: const Size(800 * 3, 360 * 3));
    await openFullScreen(tester);

    final readout = find.text('Session helper is not connected');
    final desktop = find.text('Start a stream to see the screen here');
    expect(desktop, findsOneWidget, reason: 'the empty surface did not render');

    // The surface is the aspect-ratio box the input is normalised against, which is what
    // the gesture detector fills - not the label in its middle, which is only as wide as
    // the sentence it says.
    final surface = find.ancestor(of: desktop, matching: find.byType(GestureDetector));
    expect(surface, findsOneWidget, reason: 'the input surface is not where expected');

    final readoutRect = tester.getRect(readout);
    final surfaceRect = tester.getRect(surface);
    expect(
      surfaceRect.contains(readoutRect.center),
      isTrue,
      reason: 'the readout is not over the picture in this arrangement, so nothing is being '
          'proved - the surface is ${surfaceRect.width.round()}×${surfaceRect.height.round()} '
          'at ${surfaceRect.topLeft} and the bar is at ${readoutRect.topLeft}',
    );

    // Hit-testing at the exact point rather than at a widget's own centre, because the whole
    // claim is about one specific point of the screen overlapping another specific widget.
    final hit = HitTestResult();
    WidgetsBinding.instance.hitTestInView(hit, readoutRect.center, tester.view.viewId);
    final targets = hit.path.map((entry) => entry.target).toList();

    expect(
      targets.contains(readout.evaluate().single.renderObject),
      isFalse,
      reason: 'the readout took a tap that was meant for the remote desktop; it must be '
              'skipped during hit testing, or the strip it occupies is a region of the PC '
              'the phone can no longer reach',
    );

    expect(
      targets.contains(surface.evaluate().single.renderObject),
      isTrue,
      reason: 'the tap did not reach the desktop underneath, so the bar - or the layout '
              'around it - is intercepting input that was meant for the PC',
    );
  });

  group('the things the view itself cannot prove', () {
    final screenFile = File('lib/src/ui/screens/remote_screen.dart');

    String source() {
      expect(screenFile.existsSync(), isTrue, reason: 'remote_screen.dart moved');
      return screenFile.readAsStringSync();
    }

    test('the phone is handed back, however the mode was left', () {
      // `await push` returns on any pop - the system back gesture, a swipe, the button - so
      // the `finally` is what runs. What it has to do is stated here because nothing above
      // can reach it: this code lives in the *screen*, above the view, and a regression that
      // dropped it would leave the phone rotated onto its side and its status bar hidden on
      // every other screen in the app, with no failure anywhere to say so.
      final text = source();

      expect(text.contains('await SystemChrome.setPreferredOrientations(DeviceOrientation.values);'),
          isTrue,
          reason: 'full screen locks the phone to landscape; without this restore, leaving the '
              'mode leaves every other screen locked too');
      expect(text.contains('} finally {'), isTrue,
          reason: 'the restore must run on every exit path, not just the button');
      expect(text.contains('await SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);'),
          isTrue,
          reason: 'immersiveSticky must be undone, or the status bar stays missing everywhere');
    });

    test('there is nothing to enlarge until something has arrived', () {
      // An enabled control that opens onto a black screen is worse than no control: the user
      // presses it, the screen goes full, and the only available conclusion is that the app
      // has broken rather than that the stream has not started.
      final text = source();
      expect(text.contains('onPressed: streaming ? onFullscreen : null'), isTrue,
          reason: 'the full-screen control must be tied to there being a picture to show');
    });
  });
}
