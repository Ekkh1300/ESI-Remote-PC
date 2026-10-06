/// D39 - "just click Allow" without a QR code, and without giving the machine away.
///
/// Pairing used to need two things from the user's hands: a scan of the PC's page to learn the
/// address and pin, and a second scan of the prompt to collect the one-time secret. The first
/// attempt at removing the second one failed twice on real hardware - the prompt closed on its
/// own approval (D36) and then drew a QR no camera could read (D37, D38) - and the honest
/// answer to "why is there a barcode at all" is that the barcode only ever existed to move one
/// 32-byte secret from the PC to the phone.
///
/// So the phone now mints that secret itself and sends it with `POST /pair/request`. There is
/// nothing left to transfer, and the agent still releases the credentials only to a poll
/// bearing the secret, so a device on the LAN that merely triggers a prompt still cannot
/// collect them. The agent side of that is covered by
/// `A_phone_that_brings_its_own_secret_needs_no_transfer_to_be_paired` on the C# side, against
/// a real host.
library;

import 'package:esi_remote/src/data/pairing_service.dart';
import 'package:esi_remote/src/protocol/esi_crypto.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('D39: the phone brings its own one-time secret', () {
    test('the code the phone shows is six digits, and varies', () {
      for (var i = 0; i < 50; i++) {
        final code = PairingService.makeUserCode();
        expect(code, hasLength(6));
        expect(
          RegExp(r'^\d{6}$').hasMatch(code),
          isTrue,
          reason: 'the PC prompt and this screen have to show the same shape, or the user '
              'cannot compare them',
        );
      }

      // Not a constant. Two phones showing the same digits would be indistinguishable on their
      // prompts, which is the entire job of the code.
      final codes = <String>{for (var i = 0; i < 50; i++) PairingService.makeUserCode()};
      expect(codes.length, greaterThan(1));
    });

    test('the nonce is 32 bytes and survives the base64url round trip', () {
      // The agent rejects anything that is not exactly ProtocolConstants.SecretLength, and it
      // rejects it as a bad request rather than minting a replacement - so a phone that got
      // this wrong would hold an attempt it can never unlock.
      final secret = EsiCrypto.randomBytes(32);
      final encoded = EsiCrypto.base64Url(secret);

      expect(EsiCrypto.fromBase64Url(encoded), secret);
      expect(EsiCrypto.fromBase64Url(encoded), hasLength(32));
      expect(encoded, isNot(contains('+')),
          reason: 'the value is sent as JSON and read back from a URL; base64url is what '
              'both agree on');
      expect(encoded, isNot(contains('/')));
    });
  });
}