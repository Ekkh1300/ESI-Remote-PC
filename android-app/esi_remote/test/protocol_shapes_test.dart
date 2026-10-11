/// The pairing QR parser, the query-string builder and the screen-frame parser.
///
/// The QR is the one place the app has to be liberal: PROTOCOL.md §5.4 and §9.3 say the
/// code carries the address, the pin and the provisioning secret, but no code in the agent
/// emits it, so there is no canonical encoding to match. These tests pin the shapes the
/// parser accepts, and — more importantly — pin that a partial code reports exactly which
/// field is missing instead of failing vaguely.
library;

import 'dart:convert';
import 'dart:typed_data';

import 'package:esi_remote/src/net/esi_api.dart';
import 'package:esi_remote/src/net/esi_ws.dart';
import 'package:esi_remote/src/protocol/esi_crypto.dart';
import 'package:esi_remote/src/protocol/pairing_qr.dart';
import 'package:esi_remote/src/protocol/protocol_constants.dart';
import 'package:flutter_test/flutter_test.dart';

const String _pin = 'SU5BeCIH84IaJiDJCCzPOdeKngzhZRTMcoID7N0hRc0=';
const String _secret = 'ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=';
const String _deviceId = '3f2a9c17d4b8e206';

void main() {
  group('PairingQr', () {
    test('reads a URI with the long field names', () {
      final result = PairingQr.parse(
        'esiremotepc://pair?host=192.168.1.42&port=48220&deviceId=$_deviceId'
        '&pin=$_pin&secret=$_secret&name=META-PC&mac=AA%3ABB%3ACC%3ADD%3AEE%3AFF',
      );
      expect(result.isSuccess, isTrue);
      final payload = result.payload!;
      expect(payload.host, '192.168.1.42');
      expect(payload.port, 48220);
      expect(payload.deviceId, _deviceId);
      expect(payload.spkiPin, _pin);
      expect(payload.hasSecret, isTrue);
      expect(payload.provisioningSecretBytes, hasLength(EsiProtocol.secretLength));
      expect(payload.deviceName, 'META-PC');
      expect(payload.macAddress, 'AA:BB:CC:DD:EE:FF');
      expect(payload.missingFields, isEmpty);
      expect(payload.isComplete, isTrue);
    });

    test('reads a URI with the short field names and the host in the authority', () {
      final result = PairingQr.parse('esiremotepc://192.168.1.42?k=$_pin&s=$_secret');
      expect(result.isSuccess, isTrue);
      expect(result.payload!.host, '192.168.1.42');
      expect(result.payload!.spkiPin, _pin);
      expect(result.payload!.hasSecret, isTrue);
    });

    test('a route name in the authority is not mistaken for an address', () {
      // `esiremotepc://pair?host=…` puts the word "pair" in the authority. Reading that as
      // the address would send the phone somewhere that does not exist.
      final result = PairingQr.parse('esiremotepc://pair?host=192.168.1.42&pin=$_pin');
      expect(result.payload!.host, '192.168.1.42');
    });

    test('reads a JSON object', () {
      final result = PairingQr.parse(jsonEncode(<String, dynamic>{
        'host': '192.168.1.42',
        'port': 48220,
        'deviceId': _deviceId,
        'spkiSha256': _pin,
        'provisioningSecret': _secret,
      }));
      expect(result.isSuccess, isTrue);
      expect(result.payload!.isComplete, isTrue);
      expect(result.payload!.deviceId, _deviceId);
    });

    test('reads a bare query string', () {
      final result = PairingQr.parse(
        'host=192.168.1.42&port=48220&deviceId=$_deviceId&pin=$_pin&secret=$_secret',
      );
      expect(result.isSuccess, isTrue);
      expect(result.payload!.isComplete, isTrue);
    });

    test('reads a bare address', () {
      final result = PairingQr.parse('192.168.1.42');
      expect(result.isSuccess, isTrue);
      expect(result.payload!.host, '192.168.1.42');
      // A bare address has no pin or secret, and says so by name.
      expect(result.payload!.missingFields, contains('the certificate pin'));
      expect(result.payload!.missingFields, contains('the pairing secret'));
      expect(result.payload!.isComplete, isFalse);
    });

    test('names the missing fields instead of failing vaguely', () {
      final result = PairingQr.parse('esiremotepc://pair?host=192.168.1.42&pin=$_pin');
      expect(result.isSuccess, isTrue);
      expect(result.payload!.missingFields, <String>['the pairing secret']);
    });

    test('rejects a secret of the wrong length', () {
      final result = PairingQr.parse('esiremotepc://pair?host=1.2.3.4&pin=$_pin&secret=AAAA');
      expect(result.isSuccess, isTrue);
      expect(result.payload!.hasSecret, isFalse);
      expect(result.payload!.missingFields, contains('the pairing secret'));
    });

    test('accepts a hex fingerprint and converts it to base64', () {
      // People copy pins by hand, and a terminal shows a hex digest.
      final digest = EsiCrypto.sha256(List<int>.generate(32, (i) => i));
      final hex = digest.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
      final result = PairingQr.parse('esiremotepc://pair?host=1.2.3.4&pin=$hex');
      expect(result.payload!.spkiPin, isNot(hex));
      expect(EsiCrypto.fromBase64(result.payload!.spkiPin), digest);
    });

    test('a base64 pin is left exactly as it arrived', () {
      final result = PairingQr.parse('esiremotepc://pair?host=1.2.3.4&pin=$_pin');
      expect(result.payload!.spkiPin, _pin);
    });

    test('rejects an unrelated QR code with an explanation', () {
      for (final raw in <String>[
        '',
        '   ',
        'https://example.com/some/page',
        'just some text',
        '{}',
      ]) {
        final result = PairingQr.parse(raw);
        expect(result.isSuccess, isFalse, reason: 'accepted "$raw"');
        expect(result.error, isNotNull);
        expect(result.error, isNotEmpty);
      }
    });

    test('a null pin is not mistaken for a verified one', () {
      final result = PairingQr.parse('esiremotepc://pair?host=1.2.3.4&secret=$_secret');
      expect(result.payload!.hasPin, isFalse);
      expect(result.payload!.spkiPin, isEmpty);
    });
  });

  group('EsiApi.buildQuery', () {
    test('percent-encodes a Windows path', () {
      // The agent signs the raw query bytes, so the encoding has to be produced here and
      // used unchanged on the wire.
      expect(
        EsiApi.buildQuery(<(String, String)>[('path', r'C:\Users\META\Downloads')]),
        'path=C%3A%5CUsers%5CMETA%5CDownloads',
      );
    });

    test('a space encodes as + and a literal plus as %2B', () {
      // `+` means a space in a query component, so the two have to stay distinguishable or
      // a Windows path like `C:\Program Files\App` reaches the agent corrupted.
      expect(
        EsiApi.buildQuery(<(String, String)>[('q', 'a b+c')]),
        'q=a+b%2Bc',
      );
      // Both survive a Uri round trip unchanged, which is what makes the signed bytes and
      // the sent bytes the same bytes.
      final raw = EsiApi.buildQuery(<(String, String)>[('q', 'a b+c')]);
      expect(Uri.parse('https://h/api/v1/x?$raw').toString(), 'https://h/api/v1/x?$raw');
    });

    test('an empty list is an empty query, not a bare question mark', () {
      expect(EsiApi.buildQuery(null), '');
      expect(EsiApi.buildQuery(const <(String, String)>[]), '');
    });

    test('the bytes that are signed are the bytes that are sent', () {
      // The canonical form is built from this string and the request URI is built from the
      // same one, so the two cannot drift apart.
      final raw = EsiApi.buildQuery(<(String, String)>[
        ('path', r'C:\Program Files\App'),
        ('showHidden', 'false'),
      ]);
      final uri = Uri.parse('https://192.168.1.42:48220/api/v1/files/list?$raw');
      expect(uri.toString(), 'https://192.168.1.42:48220/api/v1/files/list?$raw');
      expect(
        EsiCrypto.buildCanonicalTarget('/api/v1/files/list', raw),
        '/api/v1/files/list?path=C%3A%5CProgram+Files%5CApp&showHidden=false',
      );
    });
  });

  group('EsiEndpoint', () {
    test('always spells out the port, including the default one', () {
      // The port must never be dropped, even when it is the default. An https URL with no
      // port means 443, and the agent listens on 48220, so omitting it produced a URI that
      // cannot connect to any real install. The old version of this test asserted the
      // opposite, which is how the bug survived: the test was written to match the code
      // rather than the protocol.
      const endpoint = EsiEndpoint(host: '192.168.1.42', port: 48220, pins: <String>[]);
      expect(endpoint.apiUri('/handshake').toString(),
          'https://192.168.1.42:48220/api/v1/handshake');
      expect(endpoint.wsUri().toString(), 'wss://192.168.1.42:48220/ws');
      expect(endpoint.displayAddress, '192.168.1.42:48220');
    });

    test('the default port really is 48220, so the case above is the common one', () {
      expect(EsiProtocol.defaultHttpsPort, 48220);
    });

    test('includes a non-default port', () {
      const endpoint = EsiEndpoint(host: 'pc.local', port: 50000, pins: <String>[]);
      expect(endpoint.apiUri('/handshake').toString(), 'https://pc.local:50000/api/v1/handshake');
      expect(endpoint.wsUri().toString(), 'wss://pc.local:50000/ws');
      expect(endpoint.displayAddress, 'pc.local:50000');
    });

    test('brackets an IPv6 literal so the port is still the port', () {
      const endpoint = EsiEndpoint(host: 'fd00::5', port: 48220, pins: <String>[]);
      expect(endpoint.apiUri('/handshake').toString(),
          'https://[fd00::5]:48220/api/v1/handshake');
      expect(endpoint.displayAddress, '[fd00::5]:48220');
    });

    test('keeps the raw query in the request URI', () {
      const endpoint = EsiEndpoint(host: '1.2.3.4', port: 48220, pins: <String>[]);
      final uri = endpoint.apiUri('/files/list', 'path=C%3A%5C');
      expect(uri.query, 'path=C%3A%5C');
    });
  });

  group('EsiWsFrame', () {
    test('parses a server metrics frame', () {
      final frame = EsiWsFrame.parse(
        '{"p":1,"t":"metrics","id":null,"data":{"capturedAt":1756000000}}',
      );
      expect(frame.type, 'metrics');
      expect(frame.p, EsiProtocol.version);
      expect(frame.data!['capturedAt'], 1756000000);
    });

    test('parses an error frame', () {
      final frame = EsiWsFrame.parse(
        '{"p":1,"t":"error","id":"r3","code":"esi_forbidden_scope","message":"nope"}',
      );
      expect(frame.type, 'error');
      expect(frame.id, 'r3');
      expect(frame.code, 'esi_forbidden_scope');
      expect(frame.message, 'nope');
    });

    test('an unparseable frame has no type rather than throwing', () {
      expect(EsiWsFrame.parse('not json').type, isEmpty);
      expect(EsiWsFrame.parse('[]').type, isEmpty);
      expect(EsiWsFrame.parse('{}').type, isEmpty);
    });

    test('encodes a subscribe frame the agent will accept', () {
      final encoded = EsiWsFrame.subscribe('s1', <String>['metrics']).encode();
      final parsed = EsiWsFrame.parse(encoded);
      expect(parsed.type, 'subscribe');
      expect(parsed.id, 's1');
      expect((parsed.data!['channels'] as List<dynamic>), <String>['metrics']);
    });

    test('encodes a ping frame', () {
      final parsed = EsiWsFrame.parse(EsiWsFrame.ping('p1').encode());
      expect(parsed.type, 'ping');
      expect(parsed.id, 'p1');
    });

    test('encodes remote.start with the fields the protocol lists', () {
      // D51. These assertions used to read `parsed.data`, which is what the phone was
      // sending - and it is neither what the protocol specifies nor what the server reads.
      // WsClientFrame has no field for a nested blob on remote.start; it declares
      // displayId, fps, quality, grid and inputEnabled at the top level. So the test agreed
      // with the bug rather than catching it.
      //
      // The encoded JSON is checked directly: `parse` is our own model, and a model that
      // mirrored the mistake would satisfy the same assertion twice over.
      final raw = EsiWsFrame.remoteStart(
        id: 'r1',
        displayId: 1,
        fps: 20,
        quality: 80,
      ).encode();

      final asMap = jsonDecode(raw) as Map<String, dynamic>;
      expect(asMap['t'], 'remote.start');
      expect(asMap['displayId'], 1);
      expect(asMap['fps'], 20);
      expect(asMap['quality'], 80);
      expect(asMap['grid'], <int>[4, 4]);
      expect(asMap['inputEnabled'], isTrue);
      expect(asMap['data'], isNull,
          reason: 'these belong at the top level; nesting them under data is D51');
    });

    test('encodes a key event with a boolean down flag', () {
      final raw = EsiWsFrame.remoteKey(
        id: 'r4',
        code: 'KeyA',
        down: false,
        mods: <String>['ctrl'],
      ).encode();

      final asMap = jsonDecode(raw) as Map<String, dynamic>;
      expect(asMap['t'], 'remote.key');
      expect(asMap['code'], 'KeyA');
      expect(asMap['down'], isFalse);
      expect(asMap['mods'], <String>['ctrl']);
      expect(asMap['data'], isNull,
          reason: 'the server reads a top-level `code`; nested, every key press arrived as '
              'null and was dropped (D51)');
    });

    test('remote.input carries its payload under "input"', () {
      // The server declares exactly one field for this and it is not `data`, so a click
      // built as `data` arrived as a request with no payload at all.
      final raw = EsiWsFrame.remoteInput('r3', <String, dynamic>{
        'kind': 'mouseButton',
        'button': 'left',
        'down': true,
      }).encode();

      final asMap = jsonDecode(raw) as Map<String, dynamic>;
      expect(asMap['t'], 'remote.input');
      expect((asMap['input']! as Map)['kind'], 'mouseButton');
      expect(asMap['data'], isNull);
    });
  });

    group('screen frames', () {
    test('parses the 32-byte header', () {
      // Built by hand from the field table in §7.5 rather than captured, so the offsets
      // themselves are asserted.
      final bytes = Uint8List(32 + 14 + 3);
      final view = ByteData.sublistView(bytes);
      view.setUint32(0, 0x56495345, Endian.little); // 'E','S','I','V'
      view.setUint16(4, 1, Endian.little); // keyframe
      view.setUint16(6, 2, Endian.little); // displayId
      view.setUint32(8, 42, Endian.little); // frameIndex
      view.setUint16(12, 1, Endian.little); // tileCount
      view.setUint16(14, 1, Endian.little); // codec JPEG
      view.setInt32(16, 2560, Endian.little);
      view.setInt32(20, 1440, Endian.little);
      view.setUint16(24, 4, Endian.little);
      view.setUint16(26, 4, Endian.little);
      view.setUint32(28, 17, Endian.little); // payload length
      // One tile descriptor: col 0, row 0, 640x360, 3 bytes of JPEG.
      view.setUint16(32, 0, Endian.little);
      view.setUint16(34, 0, Endian.little);
      view.setUint16(36, 640, Endian.little);
      view.setUint16(38, 360, Endian.little);
      view.setUint32(40, 3, Endian.little);
      bytes[46] = 0xFF;
      bytes[47] = 0xD8;
      bytes[48] = 0xFF;

      final header = ScreenFrameHeader.parse(bytes);
      expect(header.frameType, ScreenFrameHeader.typeKeyframe);
      expect(header.displayId, 2);
      expect(header.frameIndex, 42);
      expect(header.tileCount, 1);
      expect(header.codec, ScreenFrameHeader.codecJpeg);
      expect(header.screenWidth, 2560);
      expect(header.screenHeight, 1440);
      expect(header.gridCols, 4);
      expect(header.gridRows, 4);
      expect(header.payloadLength, 17);

      final tiles = parseScreenTiles(bytes);
      expect(tiles, hasLength(1));
      expect(tiles.single.col, 0);
      expect(tiles.single.row, 0);
      expect(tiles.single.width, 640);
      expect(tiles.single.height, 360);
      expect(tiles.single.jpeg, <int>[0xFF, 0xD8, 0xFF]);
    });

    test('rejects a header that is too short', () {
      expect(() => ScreenFrameHeader.parse(Uint8List(31)), throwsFormatException);
    });

    test('rejects the wrong magic', () {
      final bytes = Uint8List(32);
      ByteData.sublistView(bytes).setUint32(0, 0xDEADBEEF, Endian.little);
      expect(() => ScreenFrameHeader.parse(bytes), throwsFormatException);
    });

    test('a resync request carries no tiles', () {
      final bytes = Uint8List(32);
      final view = ByteData.sublistView(bytes);
      view.setUint32(0, ScreenFrameHeader.expectedMagic, Endian.little);
      view.setUint16(4, ScreenFrameHeader.typeResyncRequest, Endian.little);
      expect(parseScreenTiles(bytes), isEmpty);
    });

    test('a tile whose length runs past the frame is dropped, not guessed at', () {
      final bytes = Uint8List(32 + 14);
      final view = ByteData.sublistView(bytes);
      view.setUint32(0, ScreenFrameHeader.expectedMagic, Endian.little);
      view.setUint16(4, ScreenFrameHeader.typeTileUpdate, Endian.little);
      view.setUint16(12, 1, Endian.little);
      view.setUint16(14, 1, Endian.little);
      view.setUint16(24, 4, Endian.little);
      view.setUint16(26, 4, Endian.little);
      view.setUint32(28, 14, Endian.little);
      view.setUint32(40, 9999, Endian.little); // claims 9999 bytes that are not there
      expect(parseScreenTiles(bytes), isEmpty);
    });
  });
}
