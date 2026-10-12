import 'package:esi_remote/src/state/connection.dart';
import 'package:esi_remote/src/state/features.dart';
import 'package:esi_remote/src/ui/widgets/setting_slider.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_api.dart';

void main() {
  group('EsiSettingSlider', () {
    Widget host({required double value, required ValueChanged<double> commit, ValueChanged<double>? preview}) =>
        MaterialApp(
          home: Scaffold(
            body: SizedBox(
              width: 300,
              child: EsiSettingSlider(
                value: value,
                min: 0,
                max: 100,
                divisions: 100,
                onCommit: commit,
                onPreview: preview,
                debounce: const Duration(milliseconds: 20),
              ),
            ),
          ),
        );

    testWidgets('the thumb follows the finger, not the server', (tester) async {
      final commits = <double>[];

      // The server says 40 and never changes. A slider bound to that value would be stuck at 40
      // for the whole drag, which is the spring-back: the thumb drags right and the server's
      // stale copy yanks it back until a reply arrives.
      await tester.pumpWidget(host(value: 40, commit: commits.add));
      final slider = find.byType(Slider);

      final gesture = await tester.startGesture(tester.getCenter(slider));
      await tester.pump();
      await gesture.moveBy(const Offset(120, 0));
      await tester.pump();
      await gesture.up();
      await tester.pump();

      expect(commits, isNotEmpty);
      expect(commits.last, greaterThan(40),
          reason: 'a drag that ends past the server value must commit the dragged value');
    });

    testWidgets('one drag produces one commit, not one per division', (tester) async {
      final commits = <double>[];

      await tester.pumpWidget(host(value: 0, commit: commits.add));
      final slider = find.byType(Slider);

      final gesture = await tester.startGesture(tester.getCenter(slider));
      await tester.pump();
      // A long drag across most of the track: well over fifty divisions of travel.
      for (var i = 0; i < 40; i++) {
        await gesture.moveBy(const Offset(6, 0));
        await tester.pump(const Duration(milliseconds: 8));
      }
      await gesture.up();
      await tester.pump(const Duration(milliseconds: 40));

      expect(commits.length, 1,
          reason: 'a drag is a gesture, not a stream of commands');
    });

    testWidgets('previews are coalesced to the debounce window', (tester) async {
      final previews = <double>[];

      await tester.pumpWidget(
        host(value: 0, commit: (_) {}, preview: previews.add),
      );
      final slider = find.byType(Slider);

      final gesture = await tester.startGesture(tester.getCenter(slider));
      await tester.pump();
      for (var i = 0; i < 30; i++) {
        await gesture.moveBy(const Offset(5, 0));
        await tester.pump(const Duration(milliseconds: 4));
      }
      await tester.pump(const Duration(milliseconds: 60));
      await gesture.up();
      await tester.pump(const Duration(milliseconds: 60));

      expect(previews, isNotEmpty);
      // Thirty moves inside a 120 ms drag with a 20 ms window cannot produce thirty previews;
      // the point is that it produces a handful and the last is the newest.
      expect(previews.length, lessThan(12),
          reason: 'previews fired ${previews.length} times for one drag');
      expect(previews.last, greaterThan(0));
    });

    testWidgets('the last preview matches the commit', (tester) async {
      final commits = <double>[];
      final previews = <double>[];

      await tester.pumpWidget(
        host(value: 0, commit: commits.add, preview: previews.add),
      );
      final slider = find.byType(Slider);
      final gesture = await tester.startGesture(tester.getCenter(slider));
      await tester.pump();
      for (var i = 0; i < 20; i++) {
        await gesture.moveBy(const Offset(7, 0));
        await tester.pump(const Duration(milliseconds: 6));
      }
      await gesture.up();
      await tester.pump(const Duration(milliseconds: 60));

      // A preview that disagrees with the commit is worse than no preview: the PC gets sent a
      // value the user never chose, then the value they did choose, and the second one has to
      // travel through the first.
      expect(commits.length, 1);
      if (previews.isNotEmpty) {
        expect(previews.last, moreOrLessEquals(commits.last, epsilon: 1.0));
      }
    });

    testWidgets('a disabled slider cannot be dragged', (tester) async {
      final commits = <double>[];

      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: SizedBox(
              width: 300,
              child: EsiSettingSlider(
                value: 50,
                min: 0,
                max: 100,
                enabled: false,
                onCommit: commits.add,
              ),
            ),
          ),
        ),
      );

      final slider = tester.widget<Slider>(find.byType(Slider));
      expect(slider.onChanged, isNull);
      expect(commits, isEmpty);
    });

    testWidgets('the server value shows again once no drag is in progress', (tester) async {
      await tester.pumpWidget(host(value: 30, commit: (_) {}));

      Slider slider() => tester.widget<Slider>(find.byType(Slider));
      expect(slider().value, 30);

      // The PC confirmed 80. With no gesture in progress the thumb must move to it.
      await tester.pumpWidget(host(value: 80, commit: (_) {}));
      await tester.pump();
      expect(slider().value, 80);
    });

    testWidgets('the thumb hands control back to the server once the commit settles', (tester) async {
      // The bug this exists for. The thumb followed the finger correctly - that part worked -
      // and then never let go: `_drag` was never cleared, so after one drag the slider showed
      // a number the PC had never agreed to, for good. The server said 0 and the thumb said 92,
      // side by side, and nothing about the screen said which one was real.
      //
      // Found by dragging it on the emulator, not by a test: eleven widget tests were green
      // because none of them ever let a commit finish.
      var server = 0.0;
      Widget app() => MaterialApp(
            home: MediaQuery(
              data: const MediaQueryData(size: Size(400, 900)),
              child: Scaffold(
                body: SizedBox(
                  width: 300,
                  child: EsiSettingSlider(
                    value: server,
                    min: 0,
                    max: 100,
                    divisions: 100,
                    onCommit: (value) async {
                      // The PC refused: volume stays where it was.
                      await Future<void>.delayed(const Duration(milliseconds: 20));
                    },
                  ),
                ),
              ),
            ),
          );

      await tester.pumpWidget(app());
      final slider = find.byType(Slider);

      final gesture = await tester.startGesture(tester.getCenter(slider));
      await tester.pump();
      await gesture.moveBy(const Offset(120, 0));
      await tester.pump();
      await gesture.up();

      // Mid-commit the thumb is still where the finger left it: that is the point of holding it.
      expect(tester.widget<Slider>(slider).value, greaterThan(0));

      await tester.pump(const Duration(milliseconds: 60));

      // And once the commit is done, it goes back to what the server says, disagreement and all.
      expect(tester.widget<Slider>(slider).value, server,
          reason: 'the thumb is still showing a value the PC never applied');
    });

    testWidgets('a server value that arrives mid-drag does not move the thumb', (tester) async {
      final commits = <double>[];

      await tester.pumpWidget(host(value: 30, commit: commits.add));
      final slider = find.byType(Slider);

      final gesture = await tester.startGesture(tester.getCenter(slider));
      await tester.pump();
      await gesture.moveBy(const Offset(100, 0));
      await tester.pump();
      await gesture.up();
      await tester.pump();

      // A refresh landing here is routine - it is what every other poll does. It must not move
      // the thumb while the finger is still on it.
      await tester.pumpWidget(host(value: 5, commit: commits.add));
      await tester.pump();

      expect(commits.last, greaterThan(30));
    });
  });

  group('MediaNotifier volume', () {
    late FakeEsiApi api;

    setUp(() => api = FakeEsiApi());

    ProviderContainer container() {
      final c = ProviderContainer(
        overrides: [apiProvider.overrideWithValue(api)],
      );
      addTearDown(c.dispose);
      return c;
    }

    test('a whole drag costs a handful of requests, not one per step', () async {
      final c = container();
      final notifier = c.read(mediaProvider.notifier);

      api.gate();

      // Ninety calls in one burst: what a finger crossing the track looks like from here.
      for (var v = 1; v <= 90; v++) {
        notifier.setVolume(v);
      }
      await Future<void>.delayed(const Duration(milliseconds: 20));

      // One request is in flight. The other eighty-nine calls overwrote a single pending value
      // rather than queueing, which is the whole point: the PC hears one value and then the
      // one the finger actually ended on.
      expect(api.volumeRequests.length, 1);
      expect(api.volumeRequests.first, 1);

      api.release();
      await Future<void>.delayed(const Duration(milliseconds: 30));

      expect(api.volumeRequests.length, 2,
          reason: 'the newest value should be sent once the first finishes');
      expect(api.volumeRequests.last, 90,
          reason: 'the value the drag ended on is the one that matters');
    });

    test('requests never overlap', () async {
      final c = container();
      final notifier = c.read(mediaProvider.notifier);

      api.gate();
      for (var v = 1; v <= 20; v++) {
        notifier.setVolume(v);
      }
      await Future<void>.delayed(const Duration(milliseconds: 20));
      expect(api.maxConcurrent, 1);

      api.release();
      await Future<void>.delayed(const Duration(milliseconds: 40));
      expect(api.maxConcurrent, 1,
          reason: 'overlapping volume posts arrive out of order and undo the drag');
    });

    test('a value asked for while one is in flight is not lost', () async {
      final c = container();
      final notifier = c.read(mediaProvider.notifier);

      api.gate();
      notifier.setVolume(10);
      await Future<void>.delayed(const Duration(milliseconds: 10));
      notifier.setVolume(77);
      api.release();
      await Future<void>.delayed(const Duration(milliseconds: 40));

      expect(api.volumeRequests.last, 77);
    });

    test('the final state reflects the last value sent, not the last to reply', () async {
      final c = container();
      final notifier = c.read(mediaProvider.notifier);

      api.gate();
      for (final v in [5, 15, 25, 35, 45]) {
        notifier.setVolume(v);
        await Future<void>.delayed(const Duration(milliseconds: 2));
      }
      api.release();
      await Future<void>.delayed(const Duration(milliseconds: 60));

      expect(c.read(mediaProvider).value?.volume, 45);
    });
  });
}
