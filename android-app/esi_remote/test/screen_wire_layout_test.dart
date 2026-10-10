import 'dart:io';
import 'dart:typed_data';

import 'package:esi_remote/src/net/esi_ws.dart';
import 'package:flutter_test/flutter_test.dart';

/// D46: every screen-stream tile failed `android.graphics.ImageDecoder` with "Input contained an
/// error", so the phone showed a permanently black stream while the agent reported the stream as
/// started.
///
/// The encoder's own JPEGs decode cleanly on Windows and begin `FF-D8-FF-E0` and end `FF-D9`, so
/// the images are good at the source. The question this test answers is whether the Dart
/// parser's view of the bytes agrees with what the server actually writes - because if the two
/// disagree by even one byte, `tileLength` slices somewhere inside the descriptor table and the
/// bytes handed to the decoder start mid-structure, which is exactly what "input contained an
/// error" means.
///
/// It reads a frame the real C# encoder produced rather than a hand-built one. A hand-built
/// fixture would only prove the parser agrees with the fixture, which is the assumption that
/// broke.
void main() {
  // The encoder probe writes here. Forward slashes: Dart's File accepts them, and it keeps the
  // backslashes out of the string so the analyzer has nothing to complain about.
  const fixturePath = 'E:/on off pc/_wire/frame.bin';
  final fixture = File(fixturePath);

  test('the Dart parser reads the same tiles the C# encoder wrote', () {
    expect(
      fixture.existsSync(),
      isTrue,
      reason: '$fixturePath is written by the encoder probe. Without it this '
          'test would only check the parser against itself, which is the assumption D46 proved '
          'wrong. Run the encoder probe first, or point this at another frame from the wire.',
    );

    final bytes = fixture.readAsBytesSync();

    // `parse` throws FormatException on a bad magic, so reaching this line is the magic check.
    final header = ScreenFrameHeader.parse(bytes);
    expect(header.codec, ScreenFrameHeader.codecJpeg);
    expect(header.gridCols, greaterThan(0));
    expect(header.gridRows, greaterThan(0));

    // The payload the header claims is exactly what followed it. A mismatch here is what a
    // mis-sized header would produce, and the tile loop below would then silently stop early.
    //
    // Note what is NOT subtracted: the header does not include the descriptor table in
    // `payloadLength`. It counts JPEG bytes only, so the wire total is
    // header + descriptors + payload, and comparing against `bytes.length - size` would be off
    // by the descriptor table - which is exactly the 224 bytes that made this test fail on its
    // first run and looked like a corrupt frame.
    expect(
      bytes.length,
      ScreenFrameHeader.size + header.tileCount * 14 + header.payloadLength,
      reason: 'header + descriptors + payload does not add up to the bytes on the wire',
    );

    final tiles = parseScreenTiles(bytes);
    expect(tiles, hasLength(header.tileCount),
        reason: 'the parser found fewer tiles than the header declares - it walked off the end '
            'of the buffer, which happens when a descriptor or payload length is misread');

    // The decisive assertion: every slice must be a real JPEG. `FF-D8` is the start-of-image
    // marker and `FF-D9` the end-of-image marker. If the slice bounds are off by anything at
    // all, the first bytes will not be the marker - which is precisely what Android's decoder
    // was complaining about.
    for (final tile in tiles) {
      expect(tile.jpeg.length, greaterThan(4),
          reason: 'tile ${tile.col},${tile.row} is implausibly small (${tile.jpeg.length} '
              'bytes), so the slice bounds are wrong');

      expect(tile.jpeg[0], 0xFF, reason: 'tile ${tile.col},${tile.row} does not start with the '
          'JPEG start-of-image marker 0xFF - the slice is offset');
      expect(tile.jpeg[1], 0xD8, reason: 'tile ${tile.col},${tile.row} does not start with the '
          'JPEG start-of-image marker 0xFFD8 - the slice is offset');

      expect(tile.jpeg[tile.jpeg.length - 2], 0xFF,
          reason: 'tile ${tile.col},${tile.row} does not end with the JPEG end-of-image marker '
              '0xFFD9 - the slice is short');
      expect(tile.jpeg[tile.jpeg.length - 1], 0xD9,
          reason: 'tile ${tile.col},${tile.row} does not end with the JPEG end-of-image marker '
              '0xFFD9 - the slice is short');
    }
  });

  test('the descriptor table is walked to exactly the end of the buffer', () {
    // This is the test that found D46. The frame is header + ALL descriptors + ALL payloads,
    // so walking descriptors and payloads with one shared offset is wrong even though every
    // individual length field reads plausibly.
    final bytes = Uint8List.fromList(fixture.readAsBytesSync());
    final header = ScreenFrameHeader.parse(bytes);
    final view = ByteData.sublistView(bytes);

    // Pass one: the whole descriptor table.
    final lengths = <int>[];
    var descriptorOffset = ScreenFrameHeader.size;
    for (var i = 0; i < header.tileCount; i++) {
      expect(descriptorOffset + 14, lessThanOrEqualTo(bytes.length),
          reason: 'descriptor $i runs past the end of the frame');
      lengths.add(view.getUint32(descriptorOffset + 8, Endian.little));
      descriptorOffset += 14;
    }

    // Pass two: the payloads start only after the last descriptor and run back to back.
    var payloadOffset = descriptorOffset;
    for (final length in lengths) {
      expect(payloadOffset + length, lessThanOrEqualTo(bytes.length),
          reason: 'a payload claims $length bytes but only '
              '${bytes.length - payloadOffset} remain');
      payloadOffset += length;
    }

    expect(payloadOffset, bytes.length,
        reason: 'the walk ended at $payloadOffset but the frame is ${bytes.length} bytes, so the '
            'descriptor layout does not match what the server wrote');
  });

  test('the first tile does not begin with another tile descriptor', () {
    // The exact shape of D46, kept as its own test because it is the one that describes the bug
    // rather than the layout. The descriptor table is followed immediately by the payload
    // region; if a reader treats the first payload byte as a descriptor it gets col=1 rather
    // than col=0, which is what happened.
    final bytes = Uint8List.fromList(fixture.readAsBytesSync());
    final tiles = parseScreenTiles(bytes);

    expect(tiles, isNotEmpty);
    expect(tiles.first.col, 0, reason: 'the first tile is column 0 by construction');
    expect(tiles.first.row, 0, reason: 'the first tile is row 0 by construction');

    // And the whole grid must come back, not just the first tile. The old parser stopped after
    // one because its offset had already drifted into the JPEG data.
    final seen = tiles.map((t) => '${t.col},${t.row}').toSet();
    expect(seen.length, tiles.length, reason: 'two tiles claim the same grid cell');
  });
}