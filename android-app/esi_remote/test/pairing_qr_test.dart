/// The pairing QR, read exactly as the Windows agent now writes it.
///
/// The agent's `PairingManager.BuildConnectUrl` emits `esiremotepc://pair?host=…&port=…&
/// deviceId=…&pin=…`, and `PairingQr.parse` lists that among the shapes it accepts. Neither
/// side can check the other's claim from its own repository, so the string below is the agent's
/// output written out literally: if the agent changes one field name, this test stops
/// describing the product and the pairing stops working.
///
/// This is also the answer to a phone that could not pair at all. The app disables Pair until
/// the pin field is non-empty, and until this QR existed no QR the agent emitted carried a pin -
/// so the only way in was to type 44 characters of base64 by hand, on a soft keyboard, where
/// `l`, `I`, `1`, `0` and `O` are indistinguishable. See ROADMAP D35.
library;

import 'package:flutter_test/flutter_test.dart';

import 'package:esi_remote/src/protocol/pairing_qr.dart';

void main() {
  group('the QR the agent emits for an idle PC', () {
    // Exactly what PairingManager.BuildConnectUrl produces, with the pin percent-encoded
    // because it ends in "=" and base64 can contain "/" and "+".
    const pin = 'f9Rl9PkFy20h7EmRGjInZguDr0eBYIEqSaO1yODVzDE=';
    const url = 'esiremotepc://pair?host=192.168.1.42&port=48220'
        '&deviceId=da704eb78efb0a3f&pin=f9Rl9PkFy20h7EmRGjInZguDr0eBYIEqSaO1yODVzDE%3D';

    test('reads back every field the pairing form needs', () {
      final result = PairingQr.parse(url);

      expect(result.isSuccess, isTrue, reason: result.error ?? 'no payload');
      final payload = result.payload!;
      expect(payload.host, '192.168.1.42');
      expect(payload.port, 48220);
      expect(payload.spkiPin, pin);
      expect(payload.deviceId, 'da704eb78efb0a3f');
      expect(payload.hasAddress, isTrue);
      expect(payload.hasPin, isTrue);
    });

    test('carries no secret, so the form knows there is nothing to collect yet', () {
      final payload = PairingQr.parse(url).payload!;

      expect(payload.hasSecret, isFalse);
      expect(payload.missingFields, contains('the pairing secret'));
      // The secret is minted per attempt, so a QR that had one would be a stale one.
      expect(payload.missingFields, isNot(contains('the certificate pin')));
    });

    test('the "pair" in the authority is not read as the address', () {
      // `esiremotepc://pair?host=…` puts a route name where a host would go. Reading that as
      // the address would send the phone somewhere that does not exist, and the QR above relies
      // on this not happening: the authority here is "pair" and the host comes from the query.
      final payload = PairingQr.parse('esiremotepc://pair?host=192.168.1.42&pin=$pin').payload!;

      expect(payload.host, '192.168.1.42');
    });

    test('an authority that really is an address is still accepted', () {
      // The other shape PairingQr documents, and the reason the check above is narrow.
      final payload =
          PairingQr.parse('esiremotepc://pair/192.168.1.42?p=48220&k=${Uri.encodeComponent(pin)}')
              .payload!;

      expect(payload.host, '192.168.1.42');
      expect(payload.port, 48220);
      expect(payload.spkiPin, pin);
    });

    test('a QR with neither address nor pin says so rather than pretending', () {
      final result = PairingQr.parse('esiremotepc://pair?deviceId=abc');

      expect(result.isSuccess, isFalse);
      expect(result.error, contains('no address, certificate pin or pairing secret'));
    });
  });
}
