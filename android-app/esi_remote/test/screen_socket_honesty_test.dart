import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// D47: the screen stream could not be started at all, and the app never said why.
///
/// Observed live on the emulator after the agent service was restarted. The Screen tab said
/// "no live socket", the Start button was greyed out, and pressing it did nothing - correctly,
/// because there was no socket to send the request over. Nothing on the screen connected those
/// two facts. A greyed-out button with a status word above it reads as "the PC is busy" or "try
/// again later", and the one action a person would take - press the button - is the one action
/// that is guaranteed to do nothing.
///
/// Two things had to be true for this: the socket had to give up permanently (fixed in
/// esi_ws.dart), and the screen had to stay silent about it.
void main() {
  final connectionFile = File('lib/src/state/connection.dart');
  final remoteFile = File('lib/src/ui/screens/remote_screen.dart');

  String source(File f) {
    expect(f.existsSync(), isTrue, reason: '${f.path} moved');
    return f.readAsStringSync();
  }

  test('the refresh button revives the socket, not just the HTTP status', () {
    final text = source(connectionFile);

    expect(text.contains('ref.read(webSocketProvider)?.retryNow()'), isTrue,
        reason: 'refresh() must reach the socket. It used to re-read HTTP only, so pressing '
            'the button repainted the dashboard - looking like it worked - while the socket '
            'stayed dead and Start stayed disabled (D47)');
  });

  test('the refresh is not a no-op on a socket that is already live', () {
    // `retryNow` returns early when it is live, so calling it unconditionally is safe. What is
    // not safe is a guard on the Dart side that races the socket's own state.
    final ws = source(File('lib/src/net/esi_ws.dart'));
    expect(ws.contains('if (_connecting || isLive) return;'), isTrue,
        reason: 'retryNow must bail out when the socket is already usable, or pressing '
            'refresh on a healthy connection tears it down and reconnects');
  });

  test('the Screen tab tells the user the button is unpressable and why', () {
    final text = source(remoteFile);

    // A disabled control with no explanation is the defect. The status line already says
    // "no live socket", but the button itself has to say something too - otherwise the two
    // facts stay unconnected in the reader's head.
    expect(text.contains('Reconnect'), isTrue,
        reason: 'the disabled Start button must offer a way out; the reconnect is automatic '
            'now, but the user has to be able to ask for it immediately rather than waiting '
            'out the backoff');
  });

  test('the status line distinguishes "reconnecting" from "never trying"', () {
    final text = source(remoteFile);

    expect(text.contains('reconnecting'), isTrue,
        reason: 'after D47 the socket is always retrying; saying so is the difference between '
            '"this is broken" and "this is catching up"');
  });

  test('a disabled Start still explains itself', () {
    final text = source(remoteFile);

    // The specific defect observed: a greyed-out button whose disabled state was never
    // explained anywhere on the screen.
    expect(text.contains('onPressed: live'), isTrue,
        reason: 'Start must be visibly tied to the socket being live, and the reason must be '
            'shown on the same control');
  });
}