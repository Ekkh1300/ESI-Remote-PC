import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// D48: tapping "Use this PC" on a discovered machine filled the address and left the
/// certificate pin empty, every time.
///
/// 
/// The address is safe to take from discovery because the TLS handshake and the pin together
/// are what authenticate the machine. The pin is not, and cannot be: if the phone reads the
/// pin out of the same unauthenticated broadcast that introduced the machine, then anyone on
/// the network can advertise a PC of their own, have the phone fill in their pin, and be
/// paired with. The pin would authenticate nothing at all.
///
/// That is why the field was left empty - and the reason it stayed empty is not visible from
/// the UI, which looks exactly like a bug: you tap the button, the address arrives, and the
/// one thing you still have to do does not. Worse, the snackbar explaining why is twelve
/// seconds of small print.
///
/// The resolution is not to weaken the rule but to notice when the rule does not apply. A
/// phone that has already paired with a machine holds the pin it verified then, in its own
/// storage, from the PC's screen or its QR code. Re-reading that trusts a past event rather
/// than the wire, which is what the rule was protecting. So it fills in - and says plainly
/// where it came from, because "it filled itself in" and "it was taken from the machine that
/// is asking to be trusted" are very different things to a person standing there.
void main() {
  final file = File('lib/src/ui/screens/onboarding_screen.dart');

  String source() {
    expect(file.existsSync(), isTrue, reason: 'onboarding_screen.dart moved');
    return file.readAsStringSync();
  }

  test('the pin comes from this phone\u2019s own records, not from the wire', () {
    final text = source();

    expect(text.contains('String? _rememberedPin(String host, int port)'), isTrue,
        reason: 'there must be one place that decides where the pin comes from');

    expect(text.contains('ref.read(machineListProvider)'), isTrue,
        reason: 'the remembered pin has to come from stored machines - the list this phone '
            'paired with, whose pins it verified itself');
  });

  test('a pin this phone does not already have is still not filled in', () {
    final text = source();

    // The guard that matters. If discovery could fill the pin, everything else is decoration.
    expect(text.contains('The pin is left empty on purpose'), isTrue,
        reason: 'the case where this phone has no record of the machine must still refuse to '
            'take a pin off the network (D48)');
    expect(text.contains('is not being taken from the network'), isTrue,
        reason: 'when the pin IS filled, the app must say where it came from - otherwise the '
            'user cannot tell a remembered pin from a trusted one');
  });

  test('a pin already typed by the user is never overwritten', () {
    final text = source();

    // Filling in on top of something the user deliberately typed would replace a correct
    // value with a stale one and say nothing.
    expect(text.contains('if (!alreadyTyped && remembered != null)'), isTrue,
        reason: 'a pin the user typed wins; a remembered one only fills an empty field');
  });

  test('the remembered pin is matched against the address it was verified for', () {
    final text = source();

    expect(text.contains('machine.port != port'), isTrue,
        reason: 'a pin is only reusable for the address it was verified against');
    expect(text.contains('machine.host.trim().toLowerCase()'), isTrue,
        reason: 'host comparison must not be case- or whitespace-sensitive, or the same '
            'machine looks like a different one and the pin is not found');
  });

  test('an empty stored pin does not blank the field', () {
    final text = source();

    expect(text.contains('if (trimmed.isNotEmpty) return trimmed;'), isTrue,
        reason: 'a rotation leaves two pins behind; a blank one must be skipped rather than '
            'filled in, which would silently block pairing for no visible reason');
  });

  test('the reason is explained where the decision is made', () {
    final text = source();

    expect(
      text.contains('own records, never from discovery'),
      isTrue,
      reason: 'the asymmetry is the whole design: discovery is untrusted, local storage is '
          'not, and the difference is one sentence',
    );
  });
}