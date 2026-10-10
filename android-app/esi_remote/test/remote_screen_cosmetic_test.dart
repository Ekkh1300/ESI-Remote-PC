import 'dart:io';

import 'package:esi_remote/src/ui/screens/remote_screen.dart';
import 'package:flutter_test/flutter_test.dart';

/// D47b: three cosmetic defects, all found by looking at the running app on the emulator
/// rather than by reading the code.
///
/// Every one of them is the same mistake in a different place: a value that is correct for the
/// machine and wrong for the person. A Windows API name shown next to English labels, a
/// reading too wide for the box reserved for it, and a frame rate printed the way Dart prints
/// doubles. None of them breaks a feature, which is exactly why they survive a test suite -
/// there is nothing to assert about until you look at it.
void main() {
  final screenFile = File('lib/src/ui/screens/remote_screen.dart');

  String source() {
    expect(screenFile.existsSync(), isTrue, reason: 'remote_screen.dart moved');
    return screenFile.readAsStringSync();
  }

  group('agent values are written for a person', () {
    test('the raw wire vocabulary is not shown', () {
      // `Capture source: gdi` sat next to "Session helper", "Maximum frame rate" and
      // "Displays" - all written for a person. One row reading as though it were meant for a
      // developer is how you lose a user's trust in the rest of the panel.
      expect(readRemoteValue('gdi'), 'Windows (GDI)');
      expect(readRemoteValue('Ready'), 'Ready');
      expect(readRemoteValue('ready'), 'Ready', reason: 'the agent may change its casing');
    });

    test('an unrecognised value is passed through, not swallowed', () {
      // The alternative - a catch-all that returns "Stopped" - means a new agent value
      // silently displays as a false claim about the machine's state.
      expect(readRemoteValue('wayland-x11'), 'wayland-x11');
    });

    test('empty and null do not become a readable word', () {
      // Turning "no value" into "Stopped" would be a statement about the PC that nothing
      // supports.
      expect(readRemoteValue(null), '');
      expect(readRemoteValue(''), '');
    });

    test('the capture panel routes its values through it', () {
      final text = source();
      expect(text.contains('readRemoteValue(status.captureSource)'), isTrue);
      expect(text.contains('readRemoteValue(status.sessionAgentState)'), isTrue);
      expect(text.contains('value: status.captureSource,'), isFalse,
          reason: 'the raw wire value is being shown again');
    });
  });

  group('slider readings fit', () {
    test('a reading cannot wrap onto two lines', () {
      final text = source();

      // The value column was 46 wide. Flutter wraps a Text that does not fit; it does not
      // ellipsize by default. So `15.0fps` rendered as `15.0f` over `ps`, and `4.0x 4` as
      // `4.0x` over `4` - which reads as two separate numbers on a settings screen.
      expect(text.contains('softWrap: false'), isTrue);
      expect(text.contains('maxLines: 1'), isTrue);
      expect(text.contains('overflow: TextOverflow.ellipsis'), isTrue);
      expect(text.contains('width: 46,'), isFalse,
          reason: '46 was the width that could not hold the longest reading');
    });

    test('the reading drops the trailing zero Dart adds', () {
      // `'$value$suffix'` gave `15.0fps`. Nobody sets fifteen point zero frames per second,
      // and the Capture panel already says `30 fps`, so one screen showed two styles for the
      // same quantity.
      expect(hasReadingGetter(), isTrue,
          reason: 'the slider must format its own reading');

      final text = source();
      expect(text.contains(r"value.round().toString()"), isTrue);
      expect(text.contains(r"value.toStringAsFixed(1)"), isTrue,
          reason: 'a genuine 13.5 fps must not round to 14');
      expect(text.contains(r"'$value\$suffix'"), isFalse,
          reason: "the unformatted interpolation is back, so '15.0fps' is showing again");
    });
  });
}

bool hasReadingGetter() {
  final text =
      File('lib/src/ui/screens/remote_screen.dart').readAsStringSync();
  return text.contains('String get _reading {');
}