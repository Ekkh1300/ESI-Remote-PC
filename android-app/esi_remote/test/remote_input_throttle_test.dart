import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// D50: the remote mouse jumped around, and there were two separate reasons.
///
/// The agent log had it in plain sight. Thirty-six `remote.input` messages in four seconds,
/// including **eight inside one millisecond**. Eight moves in a millisecond is not a person -
/// it is one drag being forwarded one pointer event at a time, with no floor on how far the
/// pointer had actually travelled. Every extra event lands on the PC as a mousemove, so the
/// remote cursor leaps through a dozen positions in a single frame.
///
/// The second reason is worse, because it is a promise the app did not keep. "Send input back"
/// was a switch. Turning it off changed a flag the agent does not read, and the input surface
/// was gated on `streaming` alone. So every touch was forwarded to the PC no matter what the
/// switch said, and the panel printed "Input is off, so touches and keys are not delivered to
/// the PC" directly underneath the switch that had just been turned off.
///
/// That sentence is the useful part. It was there, it was specific, and it was false - which
/// is worse than the jumping, because a user who believes the switch works has no reason to
/// look for why it does not.
///
/// These guards strip comments before matching. Every fix here explains the string it
/// removed, so a raw text match fails against its own documentation - which is the second time
/// today that a source-text test had to be taught that lesson.
void main() {
  final file = File('lib/src/ui/screens/remote_screen.dart');

  String source() {
    expect(file.existsSync(), isTrue, reason: 'remote_screen.dart moved');
    return file.readAsStringSync();
  }

  String code() => _stripComments(source());

  group('the input switch actually switches', () {
    test('the input surface is gated on the switch as well as the stream', () {
      final text = code();

      expect(text.contains('enabled: streaming && inputEnabled,'), isTrue,
          reason: 'the input surface must honour the switch, not just whether a stream is '
              'running (D50)');
      expect(text.contains('enabled: streaming,'), isFalse,
          reason: 'the old gate is back, and the switch does nothing again');
    });

    test('the switch value reaches the panel at all', () {
      final text = code();

      expect(text.contains('required this.inputEnabled,'), isTrue,
          reason: 'the panel never received the switch value, which is why it could not use it');
      expect(text.contains('final bool inputEnabled;'), isTrue);
      expect(text.contains('inputEnabled: _inputEnabled,'), isTrue,
          reason: 'the screen has to hand the live switch value to the panel');
    });
  });

  group('moves are neither amplified nor dropped', () {
    test('a drag is not forwarded one pointer event at a time', () {
      final text = code();

      expect(text.contains('_minMovePixels'), isTrue,
          reason: 'a finger resting on glass must not walk the remote pointer across the '
              'desktop');
      expect(text.contains('_moveIntervalMicros'), isTrue,
          reason: 'a fast flick must not become a burst of moves the PC cannot paint - the log '
              'showed eight in a single millisecond');
    });

    test('both floors have to pass, and both are needed', () {
      final text = code();

      // A slow wandering finger and a fast flick fail in opposite ways. A distance floor alone
      // still lets a fast drag through at full rate; a time floor alone still lets a resting
      // finger creep. The assertion is on both together because either one alone was the bug.
      expect(text.contains('final farEnough = travelled >= _minMovePixels;'), isTrue);
      expect(
          text.contains('final soonEnough = now - _lastMoveAtMicros >= _moveIntervalMicros;'),
          isTrue);
      expect(text.contains('if (farEnough && soonEnough)'), isTrue);
    });

    test('the first move after a pause is not swallowed', () {
      final text = code();

      // The time floor compares against the previous move, so a drag starting ten seconds
      // after the last one would be dropped entirely and the pointer would appear stuck until
      // the second event. Pressing resets it.
      expect(text.contains('_lastMoveAtMicros = 0;'), isTrue,
          reason: 'onPanStart must clear the throttle or the first drag after a pause is lost');
    });

    test('a tap still clicks, and still moves first', () {
      final text = code();

      // The throttle is for drags only. A tap is one deliberate act and must not be rate
      // limited into being ignored.
      expect(text.contains("widget.onButton('left', true, normalised.dx, normalised.dy);"),
          isTrue);
      expect(text.contains("widget.onButton('left', false, normalised.dx, normalised.dy);"),
          isTrue);
    });
  });

  group('the app does not claim things it is not doing', () {
    test('the "input is off" message sits next to the switch that controls it', () {
      final text = source();

      expect(
          text.contains('Input is off, so touches and keys are not delivered to the PC.'),
          isTrue);
      expect(text.contains('Send input back'), isTrue);

      // It was already written this way and was still false. What makes it true now is the
      // gate above; this test only records that the claim and the control live together.
    });

    test('the switch lives in the stream settings, next to what it affects', () {
      expect(source().contains("title: 'Stream settings'"), isTrue);
    });
  });
}

/// Removes line and block comments so a guard asserts on code, not on prose.
String _stripComments(String source) {
  final out = StringBuffer();
  var inLine = false;
  var inBlock = false;

  for (var i = 0; i < source.length; i++) {
    final c = source[i];

    if (inLine) {
      if (c == '\n') {
        inLine = false;
        out.write('\n');
      }
      continue;
    }

    if (inBlock) {
      if (c == '*' && i + 1 < source.length && source[i + 1] == '/') {
        inBlock = false;
        i++;
      } else if (c == '\n') {
        out.write('\n');
      }
      continue;
    }

    if (c == '/' && i + 1 < source.length && source[i + 1] == '/') {
      inLine = true;
      i++;
      continue;
    }
    if (c == '/' && i + 1 < source.length && source[i + 1] == '*') {
      inBlock = true;
      i++;
      continue;
    }

    out.write(c);
  }

  return out.toString();
}