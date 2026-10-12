import 'package:esi_remote/src/ui/widgets/nav_rail.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// The rail's shape, measured rather than eyeballed.
///
/// Two things went wrong before this file existed. `Expanded` on all five destinations handed
/// each an equal slice of a tall phone and left a finger's width of dead space between the four
/// peers. And Control - the tab people go to deliberately, from somewhere else - belongs at the
/// foot rather than in the stack with the others.
///
/// This measures [EsiNavRail] itself. An earlier version of this file reconstructed the layout
/// inside the test; it passed with the Spacer in the wrong place, which is what a copy does the
/// moment the original changes.
void main() {
  const destinations = <EsiNavDestination>[
    EsiNavDestination(
      label: 'Dashboard',
      icon: Icons.speed_outlined,
      selectedIcon: Icons.speed,
    ),
    EsiNavDestination(
      label: 'Power',
      icon: Icons.power_settings_new_outlined,
      selectedIcon: Icons.power_settings_new,
    ),
    EsiNavDestination(
      label: 'Apps',
      icon: Icons.apps_outlined,
      selectedIcon: Icons.apps,
    ),
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
    required double width,
    required double height,
    int currentIndex = 4,
    void Function(int)? onSelect,
  }) async {
    tester.view.physicalSize = Size(width, height);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        home: MediaQuery(
          data: MediaQueryData(size: Size(width, height)),
          child: Scaffold(
            body: Row(
              children: <Widget>[
                EsiNavRail(
                  destinations: destinations,
                  currentIndex: currentIndex,
                  onSelect: onSelect ?? (_) {},
                ),
                const Expanded(child: SizedBox.shrink()),
              ],
            ),
          ),
        ),
      ),
    );
  }

  List<Rect> boxes(WidgetTester tester) => <Rect>[
        for (final label in const ['Dashboard', 'Power', 'Apps', 'Screen', 'Control'])
          tester.getRect(
            find.ancestor(of: find.text(label), matching: find.byType(InkWell)),
          ),
      ];

  testWidgets('the four peers are stacked at the top with no dead space between them', (tester) async {
    await pumpRail(tester, width: 400, height: 900);

    final rects = boxes(tester);
    expect(rects.length, 5);

    for (var i = 1; i < 4; i++) {
      final gap = rects[i].top - rects[i - 1].bottom;
      expect(gap, lessThanOrEqualTo(1.0),
          reason: 'there is ${gap.toStringAsFixed(1)}px of empty rail between '
              '${rects[i - 1].center.dy} and ${rects[i].center.dy}');
    }

    // At the top, not floating in the middle. Expanded put the first one a third of the way
    // down a tall screen.
    expect(rects.first.top, lessThan(40));
  });

  testWidgets('Control sits at the foot of the rail', (tester) async {
    await pumpRail(tester, width: 400, height: 900);
    expect(find.text('Control'), findsOneWidget);
    expect(boxes(tester).last.bottom, greaterThan(900 - 60));
  });

  testWidgets('the gap above Control is the rest of the rail', (tester) async {
    await pumpRail(tester, width: 400, height: 900);
    final rects = boxes(tester);

    // This gap is meant to be large. It is the space that says "and down here is the other
    // thing", so a test that demanded small gaps everywhere would be wrong.
    expect(rects.last.top - rects[3].bottom, greaterThan(200));
  });

  testWidgets('the rail keeps its shape on a short screen', (tester) async {
    // Landscape on a phone is about 360 logical pixels tall. Five Expanded buttons fitted; the
    // pinned layout has to fit too, without overflowing and without losing the bottom item.
    await pumpRail(tester, width: 800, height: 360);

    expect(tester.takeException(), isNull);
    expect(boxes(tester).last.bottom, lessThanOrEqualTo(360));
  });

  testWidgets('a very short screen does not overflow', (tester) async {
    await pumpRail(tester, width: 800, height: 220);
    expect(tester.takeException(), isNull);
  });

  for (final scale in const <double>[1.5, 2.0]) {
    testWidgets('a button still fits its own contents at ${scale}x text', (tester) async {
      // The rail's height per entry is fixed, and the label inside it is text. At the default
      // size that fits; the question is what happens on the phone whose user has turned their
      // font size up, where a control that overflows its box loses its label - on the screen
      // that tells you which PC you are connected to.
      tester.view.physicalSize = const Size(400, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        MaterialApp(
          home: MediaQuery(
            data: MediaQueryData(
              size: const Size(400, 900),
              textScaler: TextScaler.linear(scale),
            ),
            child: Scaffold(
              body: EsiNavRail(
                destinations: destinations,
                currentIndex: 0,
                onSelect: (_) {},
              ),
            ),
          ),
        ),
      );

      expect(tester.takeException(), isNull,
          reason: 'the rail overflowed at ${scale}x text');

      // And the labels are actually all there.
      for (final label in const ['Dashboard', 'Power', 'Apps', 'Screen', 'Control']) {
        expect(find.text(label), findsOneWidget);
      }
    });
  }

  testWidgets('every destination is reachable and reports its index', (tester) async {
    final taps = <int>[];
    await pumpRail(tester, width: 400, height: 900, onSelect: taps.add);

    for (var i = 0; i < 5; i++) {
      final label = const ['Dashboard', 'Power', 'Apps', 'Screen', 'Control'][i];
      await tester.tap(find.text(label));
      await tester.pump();
    }

    expect(taps, <int>[0, 1, 2, 3, 4]);
  });

  testWidgets('the selected entry is the one the shell says it is', (tester) async {
    await pumpRail(tester, width: 400, height: 900, currentIndex: 2);
    expect(find.byIcon(Icons.apps), findsOneWidget);
    expect(find.byIcon(Icons.apps_outlined), findsNothing);
  });

  testWidgets('an offline machine still shows every entry, just dimmed', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: MediaQuery(
          data: const MediaQueryData(size: Size(400, 900)),
          child: Scaffold(
            body: EsiNavRail(
              destinations: destinations,
              currentIndex: 0,
              online: false,
              onSelect: (_) {},
            ),
          ),
        ),
      ),
    );

    // Hidden entries would be a worse failure than a dim one: the user would have no way to
    // reach the screen that shows them the machine is off.
    expect(find.text('Dashboard'), findsOneWidget);
    expect(find.text('Control'), findsOneWidget);
  });
}
