/// D33 — the pairing window must not depend on two machines having the same idea of the
/// time.
///
/// The machine that found this had a Windows Time service that had never started, and NTP
/// (UDP 123) is blocked on its network, so its clock ran 110 s behind real time. The agent
/// stamps `expiresAt` with its own clock, which put the end of the window only 10 s into
/// this phone's future; the app read that stamp straight against the phone's clock, and
/// `clamp(10, 900)` handed the whole 120 second window to its 10 second floor. The phone
/// then gave up after 9 polls reporting "The pairing window closed before it was approved"
/// while the agent was still sitting on the attempt waiting for someone to click Approve.
///
/// Four attempts on a real phone failed that way, each one polling for exactly 8.2 s. The
/// emulator paired happily throughout, because an emulator's clock is slaved to the host's
/// and so was wrong by the same 110 s.
library;

import 'package:flutter_test/flutter_test.dart';

import 'package:esi_remote/src/data/pairing_service.dart';

void main() {
  group('D33: the agent stamp is mapped onto this phone\'s clock', () {
    /// The agent creates an attempt at its own instant [create] and answers `create +
    /// window`. [lag] is how far this phone's clock runs *ahead* of the agent's, so the
    /// same creation instant is `create + lag` in this phone's seconds — which is exactly
    /// what the agent's handshake `serverTime` lets the phone measure.
    const window = 120;
    const create = 1791000000;

    test('no lag in either clock reads the full window', () {
      expect(PairingService.closesAt(create + window, 0), create + window);
    });

    test('a lagging agent clock no longer eats the window', () {
      for (final lag in const <int>[0, 1, 30, 109, 110, 119, 300, 600]) {
        final phoneCreate = create + lag;
        final expiresAt = create + window;
        final agentSkew = create - phoneCreate;

        expect(PairingService.closesAt(expiresAt, agentSkew), phoneCreate + window,
            reason: 'lag $lag s: the window must still read $window s on this phone');
      }
    });

    test('the watchdog follows the window instead of replacing it', () {
      for (final lag in const <int>[0, 110, 600]) {
        final phoneCreate = create + lag;
        final expiresAt = create + window;
        final agentSkew = create - phoneCreate;

        final closes = PairingService.closesAt(expiresAt, agentSkew);
        final deadline = PairingService.deadlineEpoch(expiresAt, agentSkew);

        expect(deadline, greaterThan(closes),
            reason: 'lag $lag s: leave the next poll room to hear the agent close it');
        expect(deadline - closes, lessThan(window),
            reason: 'lag $lag s: the margin must not outlast the window it is guarding');
      }
    });

    test('an unmeasurable skew keeps the old reading', () {
      // No handshake means no `serverTime`, and a stamp taken at face value is correct
      // exactly when the two clocks already agree — this app's earlier behaviour.
      expect(PairingService.closesAt(create + window, null), create + window);
    });

    test('the rule this replaced, for the record', () {
      // `clamp(expiresAt - now, 10, 900)` against this phone's clock, on the machine above.
      const lag = 110;
      expect((create + window - (create + lag)).clamp(10, 900), 10);
      // Which is how a 120 second pairing window became 10 seconds.
      expect(window - lag, 10);
    });
  });
}
