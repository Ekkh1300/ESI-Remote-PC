import 'package:esi_remote/src/ui/theme.dart';
import 'package:esi_remote/src/ui/widgets/nav_bar.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// The bar's shape, measured rather than eyeballed.
///
/// This replaces the rail's tests wholesale, because the rail is gone. Measuring the old
/// layout here would be worse than not measuring anything: it would pass while the phone
/// showed a bar, and it would fail on the properties the bar actually has - that the five
/// destinations sit side by side, and that the full width is left to the screen.
///
/// This measures [EsiBottomBar] itself. An earlier version of the rail file reconstructed the
/// layout inside the test; it passed with the layout in the wrong place, which is what a copy
/// does the moment the original changes.
void main() {
  const labels = <String>['Dashboard', 'Power', 'Apps', 'Screen', 'Control'];

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
    ),
  ];

  /// Laid out the way the shell lays it out: content above, bar below.
  Future<void> pumpBar(
    WidgetTester tester, {
    required double width,
    required double height,
    int currentIndex = 4,
    void Function(int)? onSelect,
    bool online = true,
    double textScale = 1.0,
  }) async {
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
            body: Column(
              children: <Widget>[
                const Expanded(child: SizedBox.shrink()),
                EsiBottomBar(
                  destinations: destinations,
                  currentIndex: currentIndex,
                  online: online,
                  onSelect: onSelect ?? (_) {},
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  List<Rect> boxes(WidgetTester tester) => <Rect>[
        for (final label in labels)
          tester.getRect(
            find.ancestor(of: find.text(label), matching: find.byType(InkWell)),
          ),
      ];

  testWidgets('the five destinations sit side by side with no dead space', (tester) async {
    await pumpBar(tester, width: 400, height: 900);

    final rects = boxes(tester);
    expect(rects.length, 5);

    // One row. The rail stacked them, so this is the property that changed.
    for (var i = 1; i < rects.length; i++) {
      expect((rects[i].top - rects[i - 1].top).abs(), lessThan(1.0),
          reason: '${labels[i]} is not on the same row as ${labels[i - 1]}');
    }

    // Contiguous and in order: no gap where a peer used to be left behind, and no slot
    // out of place. A couple of pixels of tolerance is the InkWell's own hit slop.
    for (var i = 1; i < rects.length; i++) {
      final gap = rects[i].left - rects[i - 1].right;
      expect(gap.abs(), lessThan(2.0),
          reason: '${gap.toStringAsFixed(1)}px between ${labels[i - 1]} and ${labels[i]}');
    }
  });

  testWidgets('Control is the last slot, at the right of the bar', (tester) async {
    await pumpBar(tester, width: 400, height: 900);

    final rects = boxes(tester);
    expect(rects.last.center.dx, greaterThan(rects[3].center.dx),
        reason: 'Control does not come after the four peers');
    // Rightmost, and flush with the edge of the screen.
    expect(rects.last.right, closeTo(400, 1.0));
  });

  testWidgets('the bar rests on the foot of the screen', (tester) async {
    await pumpBar(tester, width: 400, height: 900);

    final rects = boxes(tester);
    for (var i = 0; i < rects.length; i++) {
      expect(rects[i].bottom, lessThanOrEqualTo(900),
          reason: '${labels[i]} is cut off past the bottom edge');
    }
    expect(rects.last.bottom, greaterThanOrEqualTo(900 - 60),
        reason: 'Control sits ${900 - rects.last.bottom}px above the foot');
  });

  testWidgets('the screen keeps the full width the rail used to take', (tester) async {
    // The reason for the move. The rail cost 74 logical pixels of every screen on a phone
    // that is 360 wide - a fifth of the reading area, on the tab whose job is the readings.
    tester.view.physicalSize = const Size(360, 800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    // Measured as the constraints the content area is handed, not as the box of a child
    // placed in it. A `SizedBox` with no child measures zero under this layout however much
    // room there actually was - a fact about the harness rather than about the bar - and it
    // would read here as "the rail took the whole width".
    BoxConstraints? content;
    await tester.pumpWidget(
      MaterialApp(
        home: MediaQuery(
          data: const MediaQueryData(size: Size(360, 800)),
          child: Scaffold(
            body: Column(
              children: <Widget>[
                Expanded(
                  child: LayoutBuilder(
                    builder: (context, constraints) {
                      content = constraints;
                      return const SizedBox.shrink();
                    },
                  ),
                ),
                EsiBottomBar(
                  destinations: destinations,
                  currentIndex: 4,
                  onSelect: (_) {},
                ),
              ],
            ),
          ),
        ),
      ),
    );

    final measured = content;
    expect(measured, isNotNull, reason: 'the content area never laid out');

    expect(measured!.maxWidth, closeTo(360, 1.0),
        reason: 'the area above the bar is ${measured.maxWidth}px wide on a 360px phone');

    // A `Column` leaves the cross axis loose, so "how wide it *can* be" is the whole of the
    // claim: a rail would have shown up here as less than the phone. The bar has to span it
    // too, or the screen is inset on one side by the thing that moved out of the way.
    expect(tester.getSize(find.byType(EsiBottomBar)).width, closeTo(360, 1.0),
        reason: 'the bar does not span the phone, so the screen is inset after all');

    expect(measured.maxHeight + EsiBottomBar.height, closeTo(800, 1.0),
        reason: 'the bar takes ${800 - measured.maxHeight}px of the 800, leaving '
            '${measured.maxHeight}px above it');
  });

  testWidgets('the bar keeps its shape at every window size', (tester) async {
    // Ordinary phones first, then landscape, then a freeform window dragged short. A bottom
    // bar has less to get wrong than a rail did - it is one row of fixed-height slots - but
    // "less" is not "none", and an overflow here paints as a striped bar over the navigation.
    for (final height in const <double>[900, 700, 450, 360, 300, 220]) {
      await pumpBar(tester, width: 800, height: height);

      expect(tester.takeException(), isNull, reason: 'the bar overflowed at ${height}px');
      final rects = boxes(tester);
      for (var i = 0; i < rects.length; i++) {
        expect(rects[i].bottom, lessThanOrEqualTo(height),
            reason: '${labels[i]} is cut off at ${height}px');
      }
    }
  });

  for (final scale in const <double>[1.0, 1.5, 2.0, 2.5]) {
    testWidgets('every label still fits at ${scale}x text', (tester) async {
      // The label is text and text grows with the phone's font-size setting. Five slots across
      // a 360-wide phone leaves about 70 logical pixels each, which "Dashboard" does not fit
      // into once the user has turned their size up - so it scales down rather than losing the
      // tail of the word, and this is what keeps that honest.
      await pumpBar(tester, width: 360, height: 800, textScale: scale);

      expect(tester.takeException(), isNull,
          reason: 'the bar overflowed at ${scale}x text');

      for (final label in labels) {
        expect(find.text(label), findsOneWidget, reason: '$label lost its label');
        final rect = tester.getRect(find.text(label));
        // The whole word is on screen, not clipped at the edge of its own slot.
        expect(rect.width, greaterThan(0), reason: '$label collapsed to nothing');
      }

      // And the slots are still the same five, side by side, not overlapping.
      final rects = boxes(tester);
      for (var i = 1; i < rects.length; i++) {
        expect(rects[i].left, greaterThanOrEqualTo(rects[i - 1].right - 2),
            reason: '${labels[i]} overlaps ${labels[i - 1]} at ${scale}x text');
      }
    });
  }

  testWidgets('every destination is reachable and reports its index', (tester) async {
    final taps = <int>[];
    await pumpBar(tester, width: 400, height: 900, onSelect: taps.add);

    for (var i = 0; i < labels.length; i++) {
      await tester.tap(find.text(labels[i]));
      await tester.pump();
    }

    expect(taps, <int>[0, 1, 2, 3, 4]);
  });

  testWidgets('the selected entry is the one the shell says it is', (tester) async {
    await pumpBar(tester, width: 400, height: 900, currentIndex: 2);
    expect(find.byIcon(Icons.apps), findsOneWidget);
    expect(find.byIcon(Icons.apps_outlined), findsNothing);
  });

  testWidgets('an offline machine still shows every entry, just dimmed', (tester) async {
    await pumpBar(tester, width: 400, height: 900, online: false);

    // Hidden entries would be a worse failure than a dim one: the user would have no way to
    // reach the screen that shows them the machine is off.
    for (final label in labels) {
      expect(find.text(label), findsOneWidget, reason: '$label disappeared when offline');
    }

    // Control is the selected slot in this layout, so it shows its filled icon; the other
    // four show their outlines.
    const outlined = <IconData>[
      Icons.speed_outlined,
      Icons.power_settings_new_outlined,
      Icons.apps_outlined,
      Icons.desktop_windows_outlined,
    ];
    for (final icon in outlined) {
      expect(find.byIcon(icon), findsOneWidget, reason: '$icon disappeared when offline');
    }
    expect(find.byIcon(Icons.tune), findsOneWidget);

    // Dimmed is the whole of what offline changes. An entry at its normal colour would say
    // the machine is fine, which is the one thing this state exists to contradict.
    final icon = tester.widget<Icon>(find.byIcon(Icons.speed_outlined));
    expect(icon.color, EsiPalette.faint,
        reason: 'offline must dim the bar, not leave it looking healthy');
  });
}
