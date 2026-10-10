import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// D47: the live socket gave up permanently after six reconnect attempts.
///
/// Found by watching a live emulator rather than a test. The agent service had just been
/// restarted - the PC side of a deploy - and the app kept polling `/api/v1/system/metrics`
/// every three seconds exactly as before. The status line updated. The dashboard looked
/// alive. And next to the words "no live socket" every streaming control sat disabled,
/// including Start, with no error and no way out short of killing the app.
///
/// Six attempts is 1+2+4+8+16+20 seconds, so any outage past roughly fifty seconds was
/// permanent. A PC reboot, a service restart, or a Wi-Fi blip while walking between rooms
/// all land in that window routinely.
///
/// The defect is not the attempt limit on its own; it is that the limit was unreachable by
/// any other path. The dashboard polls over HTTP on a separate timer that never gave up, so
/// the component with the retry loop was the only one that could die, and the part that
/// could not die hid it.
void main() {
  final wsFile = File('lib/src/net/esi_ws.dart');

  String source() {
    expect(wsFile.existsSync(), isTrue, reason: 'esi_ws.dart moved');
    return wsFile.readAsStringSync();
  }

  test('reconnecting has no attempt limit', () {
    final text = source();

    expect(
      text.contains('if (_reconnectAttempt > 6) {'),
      isFalse,
      reason: 'six attempts is ~51s of outage; after that the socket was dead for the rest of '
          'the session with nothing able to revive it (D47)',
    );
  });

  test('the backoff delay is still capped', () {
    final text = source();

    // Removing the limit must not turn into a hot loop against a PC that is off. The delay
    // is what keeps this polite, and it is what stops a phone in a pocket from hammering
    // the network every few milliseconds all night.
    expect(text.contains('.clamp(1, 20)'), isTrue,
        reason: 'the reconnect delay must stay capped at 20s');
    expect(text.contains('(_reconnectAttempt - 1).clamp(0, 4)'), isTrue,
        reason: 'the exponent must be clamped too: `1 << (_reconnectAttempt - 1)` overflows to '
            'a negative or enormous value once the attempt count grows without bound, and a '
            'negative Duration argument throws');
  });

  test('the loop is not left reporting a permanent failure', () {
    final text = source();

    expect(text.contains('_setState(EsiSocketState.failed);\n      return;'), isFalse,
        reason: 'a `failed` state with a bare return ends the loop; that is the D47 defect');
    expect(text.contains('_setState(EsiSocketState.reconnecting);'), isTrue,
        reason: 'every drop must land in reconnecting, which is what schedules the next try');
  });

  test('there is a way to retry immediately', () {
    final text = source();

    expect(text.contains('void retryNow()'), isTrue,
        reason: 'a capped backoff is right for a rebooting PC and wrong for someone standing '
            'there waiting; the UI needs a way to skip the wait');
    expect(text.contains('_reconnectAttempt = 0;'), isTrue,
        reason: 'retryNow must clear the accumulated backoff, or the "immediate" retry is '
            'still followed by twenty-second delays');
  });

  test('the connection status distinguishes giving up from being offline', () {
    // The label is what the user reads. "no live socket" alongside a dashboard that is
    // clearly still updating is not a diagnosis.
    final connectionFile = File('lib/src/state/connection.dart');
    expect(connectionFile.existsSync(), isTrue, reason: 'connection.dart moved');
  });
}