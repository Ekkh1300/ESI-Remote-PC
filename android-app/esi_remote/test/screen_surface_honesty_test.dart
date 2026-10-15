import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// D46, third occurrence, and the one that made the first two so expensive.
/// 
/// The Screen tab showed "Nothing is arriving yet" while the agent was sending frames and every
/// one of them failed to decode. That message was not wrong - no tile was drawn - but it pointed
/// at the wrong side of the wire, exactly like the "remote capability is disabled" message in
/// D45 did. Both said something precise and true about this device while sending the reader
/// looking at the other one.
/// 
/// 
/// The predicate behind it was the cause: `hasContent` asked whether a composite existed, and a
/// composite is a black rectangle plus whatever tiles decoded, so a frame in which every tile
/// failed still counted as content. It also reported "frame 0 · 1920x1080" over a blank screen,
/// which reads like success.
/// 
/// 
/// These started as source guards on the claim that `ScreenSurface` draws through `dart:ui`
/// and so has no test host here. It does: `flutter test` runs on the real engine, and
/// `test/screen_surface_behaviour_test.dart` now decodes real bytes through `apply()` and
/// measures the predicate instead of matching its source. What is guarded below is what is
/// still only text - the sentences on the panel, which no amount of tile decoding asserts.
/// 
void main() {
  final screenFile = File('lib/src/ui/screens/remote_screen.dart');

  String source() {
    expect(screenFile.existsSync(), isTrue, reason: 'remote_screen.dart moved');
    return screenFile.readAsStringSync();
  }

  test('has_content is not merely "a composite exists"', () {
    final text = source();

    // A composite is drawn from a black rectangle, so it exists even when nothing decoded. That
    // is the whole bug: the predicate could not tell a picture from a blank canvas.
    expect(
      text.contains('bool get hasContent => _composite != null && _tiles.isNotEmpty;'),
      isTrue,
      reason: 'hasContent must require at least one decoded tile. `_composite != null` alone '
          'reports a blank screen as content, which is how D46 rendered "frame 0" over black.',
    );

    expect(
      text.contains('bool get hasContent => _composite != null;'),
      isFalse,
      reason: 'the old predicate is back, and it is the D46 defect',
    );
  });

  test('the stream panel reports how many tiles are on screen', () {
    final text = source();

    // Without a count the user cannot tell a quiet agent from unreadable bytes, and the only
    // other signal - the frame index - advances either way.
    expect(text.contains('tileCount'), isTrue,
        reason: 'ScreenSurface must expose its tile count');
    expect(text.contains('expectedTiles'), isTrue,
        reason: 'ScreenSurface must expose how many tiles the grid expects');
    expect(text.contains(r'${surface.tileCount}/${surface.expectedTiles} tiles'), isTrue,
        reason: 'the Stream panel must show how many tiles are actually on screen');
  });

  test('a frame that decoded nothing is not described as "nothing is arriving"', () {
    final text = source();

    // Two different problems with the same picture: the agent sending nothing, and the agent
    // sending something this device cannot read. They need different sentences, because the
    // first is a PC-side problem and the second is a phone-side one.
    expect(
      text.contains("arrived, but none of its "),
      isTrue,
      reason: 'a frame that arrived and failed to decode must not be reported the same way as '
          'no frame arriving; the fix belongs on this side, not the PC',
    );
  });

  test('the surface exposes the counters the panel needs', () {
    final text = source();

    expect(text.contains('int get tileCount => _tiles.length;'), isTrue);
    expect(text.contains('int get expectedTiles => _cols * _rows;'), isTrue);
  });

  test('an unavailable feature does not blame the PC for it', () {
    // D45. The message said "switched off on the PC" and pointed at the PC's settings, while the
    // handshake said `remote: true` throughout. The cause was on this side and the sentence
    // sent the reader to the wrong machine with confidence.
    final shell = File('lib/src/ui/shell.dart');
    expect(shell.existsSync(), isTrue, reason: 'shell.dart moved');

    final text = shell.readAsStringSync();

    expect(
      text.contains('is switched off on the PC'),
      isFalse,
      reason: 'this app cannot know that a capability is off on the PC until it has received the '
          'handshake, and until then it has no basis for the claim (D45)',
    );
    expect(
      text.contains('is unavailable'),
      isTrue,
      reason: 'the empty state must name what is actually true: the capability list is missing',
    );
  });
}