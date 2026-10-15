import 'dart:io';
import 'dart:typed_data';

import 'package:esi_remote/src/net/esi_ws.dart';
import 'package:esi_remote/src/ui/screens/remote_screen.dart';
import 'package:flutter_test/flutter_test.dart';

/// D46 measured rather than guarded.
///
/// `screen_surface_honesty_test.dart` guards the source text of the predicate, because it was
/// written on the claim that `ScreenSurface` draws through `dart:ui` and so has no test host.
/// It does have one: `flutter test` runs on the real engine, `instantiateImageCodec` is the
/// real codec, and `Picture.toImageSync` is the real compositor. The claim was never checked,
/// and checking it turns the part that went wrong - "is there a picture, or is there a black
/// rectangle" - from a string comparison into something that actually decodes bytes.
///
/// The distinction being measured: a composite is built from a black rectangle plus whatever
/// tiles decoded, so `_composite` is non-null either way. `image` is therefore not the
/// question. `hasContent` is, and it asks what went into the composite.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  /// A real PNG, used as a tile because the encoder that produced it is the same engine this
  /// test decodes with. No hand-written base64 to be quietly wrong about.
  late Uint8List readable;

  setUpAll(() {
    final golden = File('test/ui/goldens/shell-dashboard.png');
    expect(golden.existsSync(), isTrue,
        reason: 'the goldens double as a known-good image fixture, and they have moved');
    readable = Uint8List.fromList(golden.readAsBytesSync());
  });

  /// Bytes that are not an image. No codec accepts them, which is what a tile arriving
  /// corrupted, truncated, or from a different codec looks like on this side of the wire.
  Uint8List unreadable() => Uint8List.fromList(List<int>.filled(96, 0x00));

  ScreenFrameHeader header({
    required int frameIndex,
    int cols = 1,
    int rows = 1,
    int width = 640,
    int height = 360,
  }) =>
      ScreenFrameHeader(
        frameType: ScreenFrameHeader.typeKeyframe,
        displayId: 0,
        frameIndex: frameIndex,
        tileCount: cols * rows,
        codec: ScreenFrameHeader.codecJpeg,
        screenWidth: width,
        screenHeight: height,
        gridCols: cols,
        gridRows: rows,
        payloadLength: 0,
      );

  ScreenTile tile(int col, int row, int width, int height, Uint8List jpeg) =>
      ScreenTile(col: col, row: row, width: width, height: height, jpeg: jpeg);

  test('a frame whose every tile fails to decode has no content', () async {
    final surface = ScreenSurface();
    addTearDown(surface.dispose);

    await surface.apply(
      header: header(frameIndex: 1),
      tiles: <ScreenTile>[tile(0, 0, 640, 360, unreadable())],
    );

    // The trap itself, asserted from both sides. The composite exists - it always does, it is
    // a black rectangle first - and that is precisely why it cannot be the predicate. A screen
    // reporting "frame 1 - 640x360" over black is indistinguishable from a working one unless
    // something asks what went into the picture.
    expect(surface.image, isNotNull,
        reason: 'if the composite were null this test would not be testing the defect');
    expect(surface.hasContent, isFalse,
        reason: 'a black rectangle is not content; this is D46');
    expect(surface.tileCount, 0);
    expect(surface.expectedTiles, 1,
        reason: 'the grid is known even though nothing read');

    // The frame was still counted. A failed decode must not make the stream look frozen -
    // the two cases, "the PC is not sending" and "this phone cannot read what it sent", are
    // separated by these counters, not by the frame index.
    expect(surface.frameIndex, 1);
  });

  test('a tile that decodes is content', () async {
    final surface = ScreenSurface();
    addTearDown(surface.dispose);

    await surface.apply(
      header: header(frameIndex: 1),
      tiles: <ScreenTile>[tile(0, 0, 640, 360, readable)],
    );

    expect(surface.hasContent, isTrue, reason: 'a real tile must register as content');
    expect(surface.tileCount, 1);
    expect(surface.expectedTiles, 1);
    expect(surface.image, isNotNull);
  });

  test('a partly decoded frame is reported as what it is, not as all of it', () async {
    final surface = ScreenSurface();
    addTearDown(surface.dispose);

    await surface.apply(
      header: header(frameIndex: 1, cols: 2, rows: 1),
      tiles: <ScreenTile>[
        tile(0, 0, 320, 360, readable),
        tile(1, 0, 320, 360, unreadable()),
      ],
    );

    // Three numbers that are all true at once: there is a picture, it is one tile of two, and
    // the grid expected two. Without the middle one the panel shows a complete-looking frame
    // while half the row is stale from an earlier one.
    expect(surface.hasContent, isTrue, reason: 'a quarter of a frame is still a picture');
    expect(surface.tileCount, 1);
    expect(surface.expectedTiles, 2);
  });

  test('a later frame that fails leaves the last good picture in place', () async {
    final surface = ScreenSurface();
    addTearDown(surface.dispose);

    await surface.apply(
      header: header(frameIndex: 1),
      tiles: <ScreenTile>[tile(0, 0, 640, 360, readable)],
    );
    await surface.apply(
      header: header(frameIndex: 2),
      tiles: <ScreenTile>[tile(0, 0, 640, 360, unreadable())],
    );

    // "A tile that will not decode is left as whatever was there before, so one bad packet
    // shows a stale cell rather than a black one." One corrupt packet in a stream must not
    // blank the screen - but it must also not be mistaken for the current frame's content.
    expect(surface.hasContent, isTrue,
        reason: 'the previous picture survives a packet that could not be read');
    expect(surface.tileCount, 1);
    expect(surface.frameIndex, 2, reason: 'the frame arrived; only its bytes were unreadable');
  });

  test('a frame older than the last one is dropped', () async {
    final surface = ScreenSurface();
    addTearDown(surface.dispose);

    await surface.apply(
      header: header(frameIndex: 5),
      tiles: <ScreenTile>[tile(0, 0, 640, 360, unreadable())],
    );
    await surface.apply(
      header: header(frameIndex: 3),
      tiles: <ScreenTile>[tile(0, 0, 640, 360, readable)],
    );

    // Out-of-order packets are refused before their bytes are spent on a decode, so a
    // late frame from a network reordering cannot put an older picture on top of a newer one.
    expect(surface.tileCount, 0, reason: 'an out-of-order packet was composited anyway');
    expect(surface.hasContent, isFalse);
    expect(surface.frameIndex, 5, reason: 'the frame index must stay at the newest accepted frame');
  });

  test('a change of surface size starts a new mosaic', () async {
    final surface = ScreenSurface();
    addTearDown(surface.dispose);

    await surface.apply(
      header: header(frameIndex: 1),
      tiles: <ScreenTile>[tile(0, 0, 640, 360, readable)],
    );
    await surface.apply(
      header: header(frameIndex: 1, width: 1920, height: 1080),
      tiles: <ScreenTile>[tile(0, 0, 1920, 1080, readable)],
    );

    // A resized desktop cannot be a tile-for-tile overlay of the old one, so the map is
    // cleared rather than left with cells addressed into a grid that no longer exists.
    expect(surface.width, 1920);
    expect(surface.height, 1080);
    expect(surface.tileCount, 1, reason: 'tiles from the previous surface were kept');
    expect(surface.hasContent, isTrue);
  });
}
