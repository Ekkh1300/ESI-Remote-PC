import 'dart:convert';
import 'dart:typed_data';

import 'package:esi_remote/src/audio/mu_law.dart';
import 'package:esi_remote/src/audio/phone_speaker.dart';
import 'package:esi_remote/src/net/esi_ws.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

/// The audio half of §7.5: the same 32-byte header the screen stream uses, repurposed.
///
/// `AudioPacketiser.WritePacket` on the PC writes magic `ESIV`, frame type 5, codec 3, the
/// sample rate where the width would be, the channel count where the height would be, the
/// samples-per-channel where `gridCols` would be, and the µ-law byte count as the payload
/// length. Everything after the header is one µ-law byte per sample, interleaved.
///
/// These constants are pinned to the C# values rather than to themselves: a Dart test that
/// asserted against numbers it had just written into the fixture would pass no matter what the
/// PC actually sends.
void main() {
  // The PhoneSpeaker cases mock a platform channel, which needs the test binding. The wire
  // cases above do not, but initializing the binding is idempotent and harmless.
  TestWidgetsFlutterBinding.ensureInitialized();

  const sampleRate = 24000;
  const channels = 2;
  const samplesPerChannel = 480;
  const payloadBytes = samplesPerChannel * channels; // 960

  /// Builds the frame exactly as `AudioPacketiser.WritePacket` lays it out.
  Uint8List audioFrame({int type = 5, int codec = 3, int? declaredPayload}) {
    final frame = Uint8List(ScreenFrameHeader.size + payloadBytes);
    final view = ByteData.sublistView(frame);
    view.setUint32(0, ScreenFrameHeader.expectedMagic, Endian.little);
    view.setUint16(4, type, Endian.little);
    view.setUint16(6, 0, Endian.little); // displayId: unused for audio.
    view.setUint32(8, 7, Endian.little); // frameIndex: the sequence number.
    view.setUint16(12, 0, Endian.little); // tileCount: audio has no tiles.
    view.setUint16(14, codec, Endian.little);
    view.setInt32(16, sampleRate, Endian.little);
    view.setInt32(20, channels, Endian.little);
    view.setUint16(24, samplesPerChannel, Endian.little);
    view.setUint16(26, 0, Endian.little); // gridRows: unused.
    view.setUint32(28, declaredPayload ?? payloadBytes, Endian.little);
    // 0xFF is µ-law silence, so the payload needs no values of its own.
    for (var i = ScreenFrameHeader.size; i < frame.length; i++) {
      frame[i] = 0xFF;
    }
    return frame;
  }

  group('ScreenFrameHeader audio recognition', () {
    test('an audio frame is recognised by type AND codec together', () {
      final header = ScreenFrameHeader.parse(audioFrame());
      expect(header.frameType, ScreenFrameHeader.typeAudioChunk);
      expect(header.codec, ScreenFrameHeader.codecMuLaw);
      expect(header.isAudio, isTrue);
    });

    test('the repurposed picture fields carry the audio format', () {
      final header = ScreenFrameHeader.parse(audioFrame());
      expect(header.screenWidth, sampleRate, reason: 'width is the sample rate');
      expect(header.screenHeight, channels, reason: 'height is the channel count');
      expect(header.gridCols, samplesPerChannel, reason: 'gridCols is samples per channel');
      expect(header.payloadLength, payloadBytes, reason: 'payloadLength is the µ-law byte count');
    });

    test('a JPEG frame is not audio', () {
      final frame = audioFrame(type: ScreenFrameHeader.typeKeyframe, codec: ScreenFrameHeader.codecJpeg);
      expect(ScreenFrameHeader.parse(frame).isAudio, isFalse);
    });

    test('type 5 with a JPEG codec is not audio', () {
      // Half a match is a corrupt frame, not a new codec. Sending it down the audio path would
      // hand the decoder JPEG bytes as if they were samples.
      final frame = audioFrame(codec: ScreenFrameHeader.codecJpeg);
      expect(ScreenFrameHeader.parse(frame).isAudio, isFalse);
    });

    test('µ-law codec on a keyframe is not audio', () {
      final frame = audioFrame(type: ScreenFrameHeader.typeKeyframe);
      expect(ScreenFrameHeader.parse(frame).isAudio, isFalse);
    });
  });

  group('parseAudioChunk', () {
    test('reads the payload the header declares', () {
      final chunk = parseAudioChunk(audioFrame());
      expect(chunk, isNotNull);
      expect(chunk!.sampleRate, sampleRate);
      expect(chunk.channels, channels);
      expect(chunk.samplesPerChannel, samplesPerChannel);
      expect(chunk.muLaw, hasLength(payloadBytes));
    });

    test('the payload is silence, so the decoded samples are zero', () {
      // The end-to-end property that matters: a frame of µ-law silence decodes to PCM silence.
      // If the payload were sliced from the wrong offset the bytes would be header bytes, and
      // they decode to full-scale noise rather than to zero.
      final chunk = parseAudioChunk(audioFrame())!;
      final pcm = decodeMuLaw(chunk.muLaw);
      expect(pcm.every((s) => s == 0), isTrue,
          reason: 'µ-law silence must decode to PCM silence; a non-zero result means the '
              'payload slice started inside the header');
    });

    test('a screen frame yields null rather than throwing', () {
      final frame = audioFrame(type: ScreenFrameHeader.typeTileUpdate, codec: ScreenFrameHeader.codecJpeg);
      expect(parseAudioChunk(frame), isNull);
    });

    test('a truncated frame yields null', () {
      final full = audioFrame();
      final short = Uint8List.sublistView(full, 0, ScreenFrameHeader.size + 10);
      // The header declares 960 bytes and only 10 arrived. The chunk is clamped to what is
      // present rather than throwing - this socket also carries the screen, and one bad audio
      // packet must not take the picture stream down with it.
      final chunk = parseAudioChunk(short);
      expect(chunk, isNotNull);
      expect(chunk!.muLaw, hasLength(10));
    });

    test('a frame shorter than the header yields null', () {
      expect(parseAudioChunk(Uint8List(8)), isNull);
      expect(parseAudioChunk(Uint8List(0)), isNull);
    });

    test('a declared payload of zero yields null', () {
      expect(parseAudioChunk(audioFrame(declaredPayload: 0)), isNull);
    });

    test('a payload declared past the end of the frame is clamped', () {
      final frame = audioFrame(declaredPayload: 100000);
      final chunk = parseAudioChunk(frame);
      expect(chunk, isNotNull);
      expect(chunk!.muLaw, hasLength(payloadBytes),
          reason: 'the slice must stop at the end of the frame, not at the declared length');
      expect(() => chunk.muLaw.last, returnsNormally);
    });
  });

  group('audio frames reach the same socket as the screen', () {
    test('an audio frame does not parse as tiles', () {
      // The two parsers share one entry point in `remote_screen.dart`. An audio frame must not
      // produce tiles, or the screen path would try to decode µ-law bytes as JPEG.
      expect(parseScreenTiles(audioFrame()), isEmpty);
    });

    test('a screen frame does not parse as audio', () {
      final screen = audioFrame(type: ScreenFrameHeader.typeTileUpdate, codec: ScreenFrameHeader.codecJpeg);
      expect(parseAudioChunk(screen), isNull);
    });
  });

  group('PhoneSpeaker', () {
    late List<MethodCall> calls;
    late MethodChannel channel;

    setUp(() {
      calls = <MethodCall>[];
      channel = const MethodChannel('esi_remote/audio');
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(channel, (call) async {
        calls.add(call);
        return null;
      });
    });

    tearDown(() {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(channel, null);
    });

    test('opens the track at the format the PC announces, then writes PCM', () async {
      final speaker = PhoneSpeaker(channel: channel);
      final source = Stream<Uint8List>.fromIterable(<Uint8List>[audioFrame()]);

      await speaker.start(source);
      await Future<void>.delayed(Duration.zero);

      // The track is opened lazily, on the first frame, because the format is not known until
      // then - there is nothing to pass to `start` before it arrives.
      expect(calls.map((c) => c.method), containsAllInOrder(<String>['start', 'write']));
      final start = calls.firstWhere((c) => c.method == 'start');
      expect(start.arguments['sampleRate'], sampleRate);
      expect(start.arguments['channels'], channels);

      // µ-law silence decodes to PCM silence, so every byte written is zero. This is the
      // assertion that catches a payload slice taken from the wrong offset: header bytes decode
      // to full-scale noise, not to zero.
      final write = calls.firstWhere((c) => c.method == 'write');
      final pcm = write.arguments as Uint8List;
      expect(pcm, hasLength(payloadBytes * 2), reason: 'one 16-bit sample per µ-law byte');
      expect(pcm.every((b) => b == 0), isTrue);

      await speaker.stop();
    });

    test('a screen frame is ignored and never reaches the track', () async {
      final speaker = PhoneSpeaker(channel: channel);
      final screen = audioFrame(type: ScreenFrameHeader.typeTileUpdate, codec: ScreenFrameHeader.codecJpeg);

      await speaker.start(Stream<Uint8List>.fromIterable(<Uint8List>[screen]));
      await Future<void>.delayed(Duration.zero);

      expect(calls, isEmpty, reason: 'a screen frame must not open or write an audio track');
    });

    test('stop closes the track', () async {
      final speaker = PhoneSpeaker(channel: channel);
      await speaker.start(Stream<Uint8List>.fromIterable(<Uint8List>[audioFrame()]));
      await Future<void>.delayed(Duration.zero);
      calls.clear();

      await speaker.stop();

      expect(calls.single.method, 'stop');
      expect(speaker.isRunning, isFalse);
    });

    test('a packet is counted as dropped when the track reopens', () async {
      // A second format mid-stream reopens the track, and the packet that triggered it is
      // discarded rather than replayed stale.
      final speaker = PhoneSpeaker(channel: channel);
      final other = audioFrame(); // Same format here, so set a different one explicitly.
      final frames = Stream<Uint8List>.fromIterable(<Uint8List>[
        audioFrame(),
        // A frame announcing 48 kHz: the speaker reopens and drops this one.
        _withRate(other, 48000),
      ]);

      await speaker.start(frames);
      await Future<void>.delayed(Duration.zero);

      expect(speaker.dropped, 1);
      expect(speaker.packetsPlayed, 1);
    });
  });

  group('the audio.start frame (D87)', () {
    // The PC reads `silenceLocal` from WsClientFrame - a field of the frame itself, not of
    // any nested map. These cases pin the two shapes the PC must be able to parse: the
    // asked-for silencing on the flag, and the default off in the flag's ABSENCE, which is
    // what an older phone sent and every agent tolerated.
    test('the flag rides the frame at the top level, not inside a map', () {
      final frame = EsiWsFrame.audioStart('a+1', silenceLocal: true);
      final json = jsonDecode(frame.encode()) as Map<String, dynamic>;

      expect(json['t'], 'audio.start');
      expect(json['id'], 'a+1');
      expect(json['silenceLocal'], isTrue);
      expect(json.containsKey('data'), isFalse, reason: 'not a `data` payload - the D51 road again');
    });

    test('the default frame has no silenceLocal key at all', () {
      final json = jsonDecode(EsiWsFrame.audioStart('a+2').encode()) as Map<String, dynamic>;

      expect(json.containsKey('silenceLocal'), isFalse,
          reason: 'absent is what an older phone sent; the byte-shape must not change for a '
              'phone running with the toggle off');
    });

    test('remote.start still spreads its parameters at the top level (D51 regression)', () {
      // The D87 frame work refactored the helper remoteStart builds through; these five
      // keys sat at the top level before that refactor and must still sit there now.
      final json = jsonDecode(EsiWsFrame.remoteStart(
        id: 'r+1',
        displayId: 2,
        fps: 30,
        quality: 80,
        grid: const <int>[2, 3],
        inputEnabled: false,
      ).encode()) as Map<String, dynamic>;

      expect(json['displayId'], 2);
      expect(json['fps'], 30);
      expect(json['quality'], 80);
      expect(json['grid'], <dynamic>[2, 3]);
      expect(json['inputEnabled'], isFalse);
    });
  });
}

/// Rewrites a frame's sample rate, for the format-change case.
Uint8List _withRate(Uint8List frame, int rate) {
  final copy = Uint8List.fromList(frame);
  ByteData.sublistView(copy).setInt32(16, rate, Endian.little);
  return copy;
}
