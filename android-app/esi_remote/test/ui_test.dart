/// Widget-level tests for the honesty rules the app is built around.
///
/// These are the assertions that would be easy to break with a well-meaning refactor: that
/// an unreported sensor renders as a dash and not a number, that a destructive action
/// cannot be confirmed without saying what it does, and that the whole shell survives
/// being built with no PC paired.
library;

import 'package:esi_remote/src/data/machine_store.dart';
import 'package:esi_remote/src/protocol/models.dart';
import 'package:esi_remote/src/net/discovery.dart';
import 'package:esi_remote/src/data/pairing_service.dart';
import 'package:esi_remote/src/state/connection.dart';
import 'package:esi_remote/src/state/discovery.dart';
import 'package:esi_remote/src/state/pairing_flow.dart';
import 'package:esi_remote/src/ui/format.dart';
import 'package:esi_remote/src/ui/router.dart';
import 'package:esi_remote/src/ui/theme.dart';
import 'package:esi_remote/src/ui/widgets/load_trace.dart';
import 'package:esi_remote/src/ui/widgets/panel.dart';
import 'package:esi_remote/src/ui/widgets/states.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

/// Wraps a widget in the app's own theme so font resolution and the panel glass behave the
/// same as they do on a device.
Widget _host(Widget child) => MaterialApp(
      theme: buildEsiTheme(),
      home: Scaffold(body: child),
    );

/// Opens an attempt and then fails it, without touching a socket.
///
/// Overridden so the test owns the outcome instead of waiting on a real agent: what is under
/// test is the form's behaviour when the attempt is over, not the handshake itself.
class _FailingPairingFlow extends PairingFlowNotifier {
  @override
  Future<void> start(PairingRequest request) async {
    state = PairingFlowState(
      request: request,
      progress: const PairingProgress(phase: PairingPhase.requesting),
      startedAt: DateTime.now(),
    );
    state = state.copyWith(
      progress: const PairingProgress(
        phase: PairingPhase.failed,
        error: 'Nothing answered at that address.',
      ),
      finished: true,
    );
  }
}

void main() {
  group('formatting an unreported value', () {
    test('every nullable formatter produces the em dash', () {
      expect(formatBytes(null), notReported);
      expect(formatRate(null), notReported);
      expect(formatPercent(null), notReported);
      expect(formatCelsius(null), notReported);
      expect(formatRpm(null), notReported);
      expect(formatWatts(null), notReported);
      expect(formatMilliseconds(null), notReported);
      expect(formatUptime(null), notReported);
      expect(formatLinkSpeed(null), notReported);
      expect(formatRelative(null), notReported);
      expect(formatClock(null), notReported);
      expect(formatDeviceId(null), notReported);
    });

    test('no formatter turns a null into a zero', () {
      // The product rule: a machine with no sensor has an unknown temperature, not 0 °C.
      final formatted = <String>[
        formatBytes(null),
        formatRate(null),
        formatPercent(null),
        formatCelsius(null),
        formatRpm(null),
        formatWatts(null),
        formatUptime(null),
        formatLinkSpeed(null),
      ];
      for (final value in formatted) {
        expect(value, notReported);
        expect(value, isNot(contains('0')));
      }
    });

    test('a real zero is formatted as a real zero', () {
      // The mirror image: a genuine 0 % is a measurement and must look like one.
      expect(formatPercent(0), '0%');
      expect(formatBytes(0), '0 B');
      expect(formatRpm(0), '0 rpm');
      expect(formatCelsius(0), '0.0 °C');
    });

    test('an idle network reads as idle rather than as 0 B/s', () {
      expect(formatRate(0), 'idle');
    });

    test('sizes use binary units', () {
      expect(formatBytes(1024), '1.0 KB');
      expect(formatBytes(1536), '1.5 KB');
      expect(formatBytes(34359738368), '32.0 GB');
      expect(formatBytes(800), '800 B');
    });

    test('a long base64 pin is shortened for display, not truncated blindly', () {
      final pin = 'A' * 44;
      expect(formatBytesBase64(pin, head: 12, tail: 10), 'AAAAAAAAAAAA…AAAAAAAAAA');
      expect(formatBytesBase64(null), notReported);
    });
  });

  group('EsiReadout', () {
    testWidgets('renders a value and its unit', (tester) async {
      await tester.pumpWidget(_host(
        const EsiReadout(label: 'Memory in use', value: '15.4 GB'),
      ));
      expect(find.text('Memory in use'), findsOneWidget);
      expect(find.text('15.4 GB'), findsOneWidget);
    });

    testWidgets('renders the em dash, not a number, when the value is null', (tester) async {
      await tester.pumpWidget(_host(
        const EsiReadout(label: 'Temperature', value: null),
      ));
      expect(find.text(notReported), findsOneWidget);
      expect(find.text('0'), findsNothing);
    });

    testWidgets('a null value hides the unit rather than showing a bare unit', (tester) async {
      await tester.pumpWidget(_host(
        const EsiReadout(label: 'Clock', value: null, unit: 'MHz'),
      ));
      expect(find.text('MHz'), findsNothing);
    });
  });

  group('EsiBar', () {
    testWidgets('an unreported value draws a stub, not a partial fill', (tester) async {
      await tester.pumpWidget(_host(
        const SizedBox(
          width: 200,
          child: EsiBar(fraction: null),
        ),
      ));
      // The bar exists and is empty. What matters is that it is not 50 % full, which is
      // what a `?? 0`-style default with a 50 % floor would have produced.
      expect(find.byType(EsiBar), findsOneWidget);
    });

    testWidgets('a reported value animates in', (tester) async {
      await tester.pumpWidget(_host(
        const SizedBox(width: 200, child: EsiBar(fraction: 0.8)),
      ));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
    });

    testWidgets('a value above 1 is clamped rather than overflowing the track', (tester) async {
      await tester.pumpWidget(_host(
        const SizedBox(width: 200, child: EsiBar(fraction: 4.2)),
      ));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
    });
  });

  group('EsiCoreGrid', () {
    testWidgets('D96: two cores under identical load are labelled distinctly', (tester) async {
      // The label used to come from `chunk.indexOf(value)`, which answers with the
      // FIRST column equal to `value` - so a pair of idle cores both showed the
      // first one's index, and a busy desktop with many equal readings displayed
      // a whole run of "4 4 4 4" where board indices belonged.
      await tester.pumpWidget(_host(
        SizedBox(
          width: 400,
          child: EsiCoreGrid(
            perCore: <double>[100, 0, 0, 0, 0, 0, 0, 0],
          ),
        ),
      ));
      for (final label in <String>['0', '1', '2', '3', '4', '5', '6', '7']) {
        expect(find.text(label), findsOneWidget,
            reason: 'core $label must carry its own index');
      }
    });
  });

  group('EsiPanel', () {
    testWidgets('shows its title and children', (tester) async {
      await tester.pumpWidget(_host(
        const EsiPanel(title: 'Processor', child: Text('body')),
      ));
      expect(find.text('Processor'), findsOneWidget);
      expect(find.text('body'), findsOneWidget);
    });

    testWidgets('a footnote is rendered when given', (tester) async {
      await tester.pumpWidget(_host(
        const EsiPanel(title: 'X', footnote: 'why', child: SizedBox.shrink()),
      ));
      expect(find.text('why'), findsOneWidget);
    });
  });

  group('EsiLoadTrace', () {
    testWidgets('draws with a single sample and does not throw', (tester) async {
      await tester.pumpWidget(_host(
        const SizedBox(
          height: 120,
          child: EsiLoadTrace(
            channels: <TraceChannel>[
              TraceChannel(label: 'CPU', series: <double>[42], colour: EsiPalette.signal),
            ],
          ),
        ),
      ));
      await tester.pump();
      expect(tester.takeException(), isNull);
    });

    testWidgets('a gap in the data is not interpolated across', (tester) async {
      // The agent returns NaN for a sample it could not take. Bridging the gap would draw
      // a trend that was never measured.
      await tester.pumpWidget(_host(
        const SizedBox(
          height: 120,
          child: EsiLoadTrace(
            channels: <TraceChannel>[
              TraceChannel(
                label: 'CPU',
                series: <double>[10, double.nan, 30],
                colour: EsiPalette.signal,
              ),
            ],
          ),
        ),
      ));
      await tester.pump();
      expect(tester.takeException(), isNull);
    });

    testWidgets('says so when there is nothing to draw', (tester) async {
      await tester.pumpWidget(_host(
        const SizedBox(height: 120, child: EsiLoadTrace(channels: <TraceChannel>[])),
      ));
      expect(find.text('No load data yet'), findsOneWidget);
    });

    test('describes itself in words for a screen reader', () {
      final description = describeTrace(<TraceChannel>[
        TraceChannel(label: 'CPU', series: <double>[10, 42], colour: EsiPalette.signal),
        TraceChannel(label: 'GPU', series: <double>[5], colour: EsiPalette.flare),
      ]);
      expect(description, 'CPU 42%, GPU 5%');
    });

    testWidgets('says so when every channel has only the one reading', (tester) async {
      // The gap this closes. The grid and the legend are painted either way, so a plot holding
      // a single sample looks identical to a plot that is failing - a frame, three labels, and
      // nothing in between - and "the graph does not show" is the only thing the user can say
      // about either. One sample draws a dot and no line, so the panel says so.
      await tester.pumpWidget(_host(
        const SizedBox(
          height: 120,
          child: EsiLoadTrace(
            channels: <TraceChannel>[
              TraceChannel(label: 'CPU', series: <double>[42], colour: EsiPalette.signal),
              TraceChannel(label: 'GPU', series: <double>[17], colour: EsiPalette.flare),
              TraceChannel(label: 'RAM', series: <double>[63], colour: EsiPalette.lock),
            ],
          ),
        ),
      ));
      expect(find.text('Taking the first readings'), findsOneWidget);
      expect(find.text('No load data yet'), findsNothing);
    });

    testWidgets('says nothing once a line can be drawn', (tester) async {
      // One channel with two readings is enough; the other two catching up is not the
      // dashboard's problem to announce.
      await tester.pumpWidget(_host(
        const SizedBox(
          height: 120,
          child: EsiLoadTrace(
            channels: <TraceChannel>[
              TraceChannel(label: 'CPU', series: <double>[42, 51], colour: EsiPalette.signal),
              TraceChannel(label: 'GPU', series: <double>[17], colour: EsiPalette.flare),
            ],
          ),
        ),
      ));
      expect(find.text('Taking the first readings'), findsNothing);
      expect(find.text('No load data yet'), findsNothing);
    });

    test('a reading it could not take is not a reading', () {
      // The painter skips NaN instead of bridging it, so two samples where one of them is
      // NaN is still one sample, and claiming otherwise would promise a line that is not there.
      expect(
        traceState(<TraceChannel>[
          TraceChannel(label: 'CPU', series: <double>[double.nan, double.nan], colour: EsiPalette.signal),
        ]),
        TraceState.empty,
      );
      expect(
        traceState(<TraceChannel>[
          TraceChannel(label: 'CPU', series: <double>[10, double.nan, 30], colour: EsiPalette.signal),
        ]),
        TraceState.ready,
      );
      expect(
        traceState(<TraceChannel>[
          TraceChannel(label: 'CPU', series: <double>[10], colour: EsiPalette.signal),
        ]),
        TraceState.thin,
      );
      expect(traceState(const <TraceChannel>[]), TraceState.empty);
    });
  });

  group('EsiConfirmDialog', () {
    testWidgets('says what will happen, not just what the button says', (tester) async {
      await tester.pumpWidget(_host(
        Builder(
          builder: (context) => TextButton(
            onPressed: () => EsiConfirmDialog.show(
              context,
              title: 'Restart',
              body: 'Windows will close applications that ask it to.',
              confirmLabel: 'Restart',
            ),
            child: const Text('open'),
          ),
        ),
      ));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();

      expect(find.text('Restart'), findsWidgets);
      expect(
        find.text('Windows will close applications that ask it to.'),
        findsOneWidget,
      );
    });

    testWidgets('a typed confirmation keeps the button disabled until it matches',
        (tester) async {
      await tester.pumpWidget(_host(
        Builder(
          builder: (context) => TextButton(
            onPressed: () => EsiConfirmDialog.show(
              context,
              title: 'Shut down',
              body: 'The machine powers off.',
              confirmLabel: 'Shut down',
              destructive: true,
              requiresTyping: 'SHUTDOWN',
            ),
            child: const Text('open'),
          ),
        ),
      ));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();

      final button = find.widgetWithText(FilledButton, 'Shut down');
      expect(tester.widget<FilledButton>(button).onPressed, isNull);

      await tester.enterText(find.byType(TextField), 'shutdown');
      await tester.pumpAndSettle();
      expect(tester.widget<FilledButton>(button).onPressed, isNotNull);
    });

    testWidgets('cancelling returns false', (tester) async {
      bool? result;
      await tester.pumpWidget(_host(
        Builder(
          builder: (context) => TextButton(
            onPressed: () async {
              result = await EsiConfirmDialog.show(
                context,
                title: 'Delete',
                body: 'Gone for good.',
                confirmLabel: 'Delete',
                destructive: true,
              );
            },
            child: const Text('open'),
          ),
        ),
      ));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(result, isFalse);
    });
  });

  group('empty and error states', () {
    testWidgets('an empty state offers the one action that would fix it', (tester) async {
      var tapped = false;
      await tester.pumpWidget(_host(
        EsiEmptyState(
          title: 'No PC paired yet',
          body: 'Pair with a PC before using this screen.',
          action: FilledButton(
            onPressed: () => tapped = true,
            child: const Text('Find a PC'),
          ),
        ),
      ));
      expect(find.text('No PC paired yet'), findsOneWidget);
      await tester.tap(find.text('Find a PC'));
      expect(tapped, isTrue);
    });

    testWidgets('an error state shows the reason and retries', (tester) async {
      var retried = false;
      await tester.pumpWidget(_host(
        EsiErrorView(
          message: 'No answer from 192.168.1.42.',
          detail: 'The phone could not complete a TLS handshake.',
          onRetry: () => retried = true,
        ),
      ));
      expect(find.textContaining('No answer from'), findsOneWidget);
      expect(find.textContaining('could not complete a TLS handshake'), findsOneWidget);
      await tester.tap(find.text('Try again'));
      expect(retried, isTrue);
    });
  });

  group('app shell', () {
    testWidgets('builds with nothing paired and says so', (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            machineStoreProvider.overrideWithValue(
              MachineStore.inMemory(<String, String>{}),
            ),
            // No sockets in a widget test: the search is replaced with an instant,
            // empty answer.
            discoveryRunnerProvider.overrideWithValue(
              ({onProgress}) async => const DiscoveryResult(
                [],
                methods: <DiscoveryMethod>{},
                notes: <String>['Nothing answered in this test.'],
              ),
            ),
          ],
          child: const EsiRemoteApp(),
        ),
      );
      // Fixed pumps rather than pumpAndSettle: the onboarding screen shows a progress
      // indicator while a search is running, and a live indicator never settles.
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      await tester.pump(const Duration(milliseconds: 400));

      // Onboarding, not a crash and not a blank screen.
      expect(find.text('ESI Remote PC'), findsWidgets);
      expect(find.text('On this network'), findsOneWidget);
      expect(find.text('Add a PC by hand'), findsOneWidget);
      expect(find.text('Nothing answered in this test.'), findsOneWidget);
    });

    testWidgets('offers the three ways in, and the QR route explains the secret',
        (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            machineStoreProvider.overrideWithValue(
              MachineStore.inMemory(<String, String>{}),
            ),
            discoveryRunnerProvider.overrideWithValue(
              ({onProgress}) async => const DiscoveryResult(
                [],
                methods: <DiscoveryMethod>{},
                notes: <String>[],
              ),
            ),
          ],
          child: const EsiRemoteApp(),
        ),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));

      // The QR route is below the fold, so it is scrolled to rather than assumed visible.
      await tester.dragUntilVisible(
        find.text('Pairing code'),
        find.byType(ListView),
        const Offset(0, -200),
      );
      await tester.pump();

      expect(find.text('Pairing code'), findsOneWidget);
      expect(find.text('Scan'), findsOneWidget);
      expect(find.text('Paste'), findsOneWidget);
      // The copy has to be honest about *why* the QR is the good path.
      expect(find.textContaining('one-time secret'), findsWidgets);
    });

    testWidgets('a failed attempt hands the form back with both buttons live',
        (tester) async {
      // `_start` on the form awaits the future `context.push('/pairing')` returned, and only
      // that future's `finally` clears the `_busy` that greys these buttons out. Leaving the
      // attempt with `context.go` replaced the stack instead of popping, so the future was
      // abandoned and the form came back dead until the app was restarted. This drives the
      // real path - fill, Pair, fail, Start again - and asks for the buttons afterwards.
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            machineStoreProvider.overrideWithValue(
              MachineStore.inMemory(<String, String>{}),
            ),
            discoveryRunnerProvider.overrideWithValue(
              ({onProgress}) async => const DiscoveryResult(
                [],
                methods: <DiscoveryMethod>{},
                notes: <String>[],
              ),
            ),
            pairingFlowProvider.overrideWith(_FailingPairingFlow.new),
          ],
          child: const EsiRemoteApp(),
        ),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));

      Finder byLabel(String label) => find.byWidgetPredicate(
            (w) => w is TextField && w.decoration?.labelText == label,
          );

      // The form sits below the fold, so it is scrolled to rather than assumed built.
      for (var i = 0; i < 12 && byLabel('Certificate pin').evaluate().isEmpty; i++) {
        await tester.drag(find.byType(ListView).first, const Offset(0, -300));
        await tester.pump(const Duration(milliseconds: 100));
      }
      expect(byLabel('Certificate pin'), findsOneWidget);

      await tester.enterText(byLabel('Address'), '192.168.1.50');
      await tester.pump();
      await tester.enterText(byLabel('Certificate pin'), 'aGVsbG8gd29ybGQ');
      await tester.pump();

      expect(
        tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Pair')).onPressed,
        isNotNull,
        reason: 'a filled-in form offers Pair',
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Pair'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 300));
      await tester.tap(find.text('Start pairing'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pump(const Duration(milliseconds: 300));

      // The attempt failed and says so.
      expect(find.text('Start again'), findsOneWidget);

      await tester.tap(find.text('Start again'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 300));

      expect(find.text('Add a PC by hand'), findsOneWidget);
      expect(
        tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Pair')).onPressed,
        isNotNull,
        reason: 'Pair must be live again: the attempt is over, so _busy has to be cleared',
      );
      expect(
        tester.widget<OutlinedButton>(
          find.widgetWithText(OutlinedButton, 'Check address'),
        ).onPressed,
        isNotNull,
        reason: 'Check address is gated on _busy alone, so it is the readout of that flag',
      );
    });
  });

  group('severity', () {
    test('a null reading is unknown, not nominal', () {
      expect(severityForPercent(null), Severity.unknown);
      expect(severityForCelsius(null), Severity.unknown);
      expect(severityForDisk(null), Severity.unknown);
    });

    test('thresholds are ordered', () {
      expect(severityForPercent(10), Severity.nominal);
      expect(severityForPercent(75), Severity.elevated);
      expect(severityForPercent(95), Severity.critical);
      expect(severityForCelsius(50), Severity.nominal);
      expect(severityForCelsius(80), Severity.elevated);
      expect(severityForCelsius(95), Severity.critical);
    });
  });

  group('models round trip through the UI-facing accessors', () {
    test('a metrics snapshot with no sensors produces dashes everywhere', () {
      final snapshot = MetricsSnapshot.fromJson(<String, dynamic>{
        'cpu': <String, dynamic>{'usagePercent': 0, 'temperatureC': null},
        'memory': <String, dynamic>{'usedBytes': 0, 'totalBytes': 0, 'usedPercent': 0},
        'gpu': <Map<String, dynamic>>[],
        'storage': <Map<String, dynamic>>[],
        'network': <String, dynamic>{'interfaces': <Map<String, dynamic>>[]},
        'gpuAggregate': <String, dynamic>{'usagePercent': 0, 'maxTemperatureC': null},
      });
      expect(formatCelsius(snapshot.cpu.temperatureC), notReported);
      expect(formatCelsius(snapshot.gpuAggregate.maxTemperatureC), notReported);
      expect(formatBytes(snapshot.network.primary == null ? null : 0), notReported);
      // Zeroes that *are* reported stay zeroes.
      expect(formatPercent(snapshot.cpu.usagePercent), '0%');
    });

    test('a Handshake with no features greys out everything', () {
      final handshake = Handshake.fromJson(<String, dynamic>{'deviceId': 'x'});
      expect(handshake.supports('power'), isFalse);
      expect(handshake.supports('anythingAtAll'), isFalse);
    });
  });
}
