import 'package:esi_remote/src/ui/theme.dart';
import 'package:esi_remote/src/ui/widgets/nav_rail.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// Control must be at the foot of the rail on *every* window, not only on the tall one.
///
/// The first version of the rail chose between two layouts using `needed`, a height computed
/// from the fixed `buttonHeight` constant. That is a prediction about widgets whose real height
/// is measured later: the buttons use `minHeight`, and their contents are text, which grows
/// with the user's font-size setting. When the prediction said "there is room", the roomy layout
/// ran; when it said "there is not", the other one ran - and the other one had no Spacer, so
/// Control sat immediately under Screen with the rest of the rail's empty space below it.
///
/// Both outcomes are the same complaint from the outside: the rail did not put Control at the
/// bottom. So this file measures the one property that matters across a range of window heights
/// and text scales, and does not care which layout produced it.
void main() {
  const destinations = <EsiNavDestination>[
    EsiNavDestination(label: 'Dashboard', icon: Icons.speed_outlined, selectedIcon: Icons.speed),
    EsiNavDestination(
      label: 'Power',
      icon: Icons.power_settings_new_outlined,
      selectedIcon: Icons.power_settings_new,
    ),
    EsiNavDestination(label: 'Apps', icon: Icons.apps_outlined, selectedIcon: Icons.apps),
    EsiNavDestination(
      label: 'Screen',
      icon: Icons.desktop_windows_outlined,
      selectedIcon: Icons.desktop_windows,
    ),
    EsiNavDestination(
      label: 'Control',
      icon: Icons.tune_outlined,
      selectedIcon: Icons.tune,
      pinnedToBottom: true,
    ),
  ];

  Future<void> pumpRail(
    WidgetTester tester, {
    required double height,
    double textScale = 1.0,
  }) async {
    const width = 400.0;
    tester.view.physicalSize = Size(width, height);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        home: MediaQuery(
          data: MediaQueryData(
            size: Size(width, height),
            textScaler: TextScaler.linear(textScale),
          ),
          child: Scaffold(
            body: Row(
              children: <Widget>[
                EsiNavRail(
                  destinations: destinations,
                  currentIndex: 4,
                  onSelect: (_) {},
                ),
                const Expanded(child: SizedBox.shrink()),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Rect controlRect(WidgetTester tester) => tester.getRect(
        find.ancestor(of: find.text('Control'), matching: find.byType(InkWell)),
      );

  // Window heights chosen to cross whatever threshold the layout branches on. 900 and 700 are
  // ordinary phones; 450 is roughly landscape; 360 is landscape on a phone; 300 and 220 are a
  // freeform window dragged short. The old code was roomy above about 360 and not below it.
  const heights = <double>[900, 700, 450, 360, 300, 220];

  group('Control is at the foot of the rail, at every window height', () {
    for (final height in heights) {
      testWidgets('${height}px tall', (tester) async {
        await pumpRail(tester, height: height);

        expect(tester.takeException(), isNull, reason: 'the rail overflowed at ${height}px');
        final control = controlRect(tester);

        // Above the bottom edge - not clipped past it, which is what happens when the roomy
        // layout runs on a height that does not actually have room for it.
        expect(control.bottom, lessThanOrEqualTo(height),
            reason: 'Control is cut off at ${height}px');
        // And resting on the foot, within the bottom padding of the rail. The old spacerless
        // layout left it anywhere up to a whole screen higher than this.
        expect(control.bottom, greaterThanOrEqualTo(height - EsiSpace.sm - 1),
            reason: 'Control sits at y=${control.bottom} on a ${height}px rail, which is '
                '${height - control.bottom}px above the foot');
      });
    }
  });

  group('Control is at the foot of the rail, at every font size', () {
    for (final scale in const <double>[1.0, 1.5, 2.0, 2.5]) {
      testWidgets('${scale}x text', (tester) async {
        await pumpRail(tester, height: 900, textScale: scale);

        expect(tester.takeException(), isNull, reason: 'the rail overflowed at ${scale}x text');
        final control = controlRect(tester);

        expect(control.bottom, lessThanOrEqualTo(900), reason: 'cut off at ${scale}x text');
        expect(control.bottom, greaterThanOrEqualTo(900 - EsiSpace.sm - 1),
            reason: 'Control sits ${900 - control.bottom}px above the foot at ${scale}x text');
      });
    }
  });

  testWidgets('the four peers stay stacked above Control on a short window', (tester) async {
    // The failure is easy to "fix" by pinning Control and scattering the rest. The peers have to
    // remain a contiguous block, because that grouping is the other half of what the rail says.
    await pumpRail(tester, height: 360);

    Rect box(String label) => tester.getRect(
          find.ancestor(of: find.text(label), matching: find.byType(InkWell)),
        );

    final order = <String>['Dashboard', 'Power', 'Apps', 'Screen'];
    for (var i = 1; i < order.length; i++) {
      expect(box(order[i]).top, greaterThanOrEqualTo(box(order[i - 1]).bottom - 1),
          reason: '${order[i]} is not directly under ${order[i - 1]}');
    }
    expect(box('Control').top, greaterThanOrEqualTo(box('Screen').bottom - 1),
        reason: 'Control belongs after the peers, never among or above them');

    // The gap *between* them is only meaningful when there is room for one, and a 360px window
    // has none to spare: the pinned entry still lands at the foot with nothing under it. That
    // gap is measured on a tall window in nav_rail_test.dart, where it is supposed to be large.
  });
}
