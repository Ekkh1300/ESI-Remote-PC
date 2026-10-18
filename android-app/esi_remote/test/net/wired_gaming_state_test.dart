import 'package:esi_remote/src/net/esi_ws.dart';
import 'package:flutter_test/flutter_test.dart';

/// The typed stream taps over the raw frame stream, mostly trivially - and D89 found
/// `gamingStates` the one tap where a frame WAS data but carries no state at all.
///
/// The hub answers a `gaming.watch` request with a `gaming` frame whose `data` is `null`:
/// a receipt, not a reading. Reading that receipt as state handed `GamingState.fromJson({})`
/// to a parser that turns every missing number into a zero, and a subscribed gaming screen
/// watched its own GPU read 0% the instant it opened - with the real numbers available on
/// the wire one frame later, if any real frame ever arrived.
void main() {
  group('EsiWsStream.gamingStates (D89)', () {
    test('a data-less gaming frame is an ack and is never read as state', () async {
      final raw = Stream<EsiWsFrame>.fromIterable(<EsiWsFrame>[
        // The watch receipt: type `gaming`, no data.
        const EsiWsFrame(p: 1, type: 'gaming', id: 'g1'),
        const EsiWsFrame(p: 1, type: 'ping', id: 'x'),
      ]);

      expect(await raw.gamingStates.toList(), isEmpty);
    });

    test('a state-bearing gaming frame still lands', () async {
      final raw = Stream<EsiWsFrame>.fromIterable(<EsiWsFrame>[
        const EsiWsFrame(p: 1, type: 'gaming', id: 'g1'),
        const EsiWsFrame(
          p: 1,
          type: 'gaming',
          id: 'g2',
          data: <String, dynamic>{
            'mode': 'game',
            'fps': 0,
            'cpuUsagePercent': 12.0,
            'gpuUsagePercent': 61.5,
            'suspendedApps': <Object>[],
            'history': <Object>[],
          },
        ),
      ]);

      final states = await raw.gamingStates.toList();
      expect(states, hasLength(1));
      expect(states.single.gpuUsagePercent, 61.5);
    });
  });
}
