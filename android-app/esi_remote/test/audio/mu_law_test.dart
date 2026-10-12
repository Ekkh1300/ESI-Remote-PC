import 'dart:typed_data';

import 'package:esi_remote/src/audio/mu_law.dart';
import 'package:flutter_test/flutter_test.dart';

/// The PC's audio codec, implemented on the phone.
///
/// The fixture below is the same one `MuLawTests` in the C# suite pins. Two implementations of
/// one curve in two languages can each be perfectly self-consistent and disagree with each
/// other, and neither suite would notice because each only decodes what it encoded — this is the
/// test that would catch it.
void main() {
  group('decodeOne', () {
    test('every one of the 256 codes lands in 16-bit range', () {
      for (var code = 0; code < 256; code++) {
        final value = decodeOne(code);
        expect(value, inInclusiveRange(-32768, 32767), reason: 'code $code');
      }
    });

    test('silence is the code 0xFF, not zero', () {
      // A codec whose zero is 0x00 is a codec whose silence depends on what the transport left
      // in the buffer. µ-law puts silence at the top of the range so that uninitialised memory
      // is noise rather than silence.
      expect(decodeOne(0xFF), 0);
      for (var i = 1; i < 64; i++) {
        expect(decodeOne(i) != 0, isTrue, reason: 'code $i is near silence but not silence');
      }
    });

    test('the sign bit is inverted, so the loudest codes are at the ends', () {
      // 0x80 is the loudest positive and 0x00 the loudest negative, which is what the encoder's
      // `^ mask` produces. Getting this backwards plays the stream upside down.
      final positive = decodeOne(0x80);
      final negative = decodeOne(0x00);
      expect(positive, greaterThan(30000));
      expect(negative, lessThan(-30000));
      expect(positive, -negative);
    });

    test('the curve is symmetric about zero', () {
      for (var code = 0; code < 128; code++) {
        final a = decodeOne(code);
        // The sign is the top bit and it is stored inverted, so the mirror of a code is that
        // same code with the one bit flipped. Subtracting from 0x7F instead would change the
        // segment and the mantissa as well, and the test would fail against a correct decoder.
        final b = decodeOne(code ^ 0x80);
        expect(a, -b, reason: 'code $code and its mirror');
      }
    });
  });

  group('decodeMuLaw', () {
    test('one sample per byte', () {
      final decoded = decodeMuLaw(Uint8List(1000));
      expect(decoded.length, 1000);
    });

    test('writes into a caller-supplied buffer at an offset', () {
      // The streaming path decodes straight into a reusable ring buffer rather than allocating
      // a fresh list per packet, which at 50 packets a second is the difference between a
      // steady heap and a sawtooth one.
      final into = Int16List(2048);
      for (var i = 0; i < into.length; i++) {
        into[i] = -1;
      }
      final source = Uint8List.fromList(List.filled(512, 0xFF));
      decodeMuLaw(source, into: into, offset: 1024);

      expect(into.sublist(0, 1024).every((v) => v == -1), isTrue,
          reason: 'the write ran backwards over the buffer');
      expect(into.sublist(1024, 1536).every((v) => v == 0), isTrue);
      expect(into.sublist(1536).every((v) => v == -1), isTrue,
          reason: 'the write ran past the end of the source');
    });

    test('rejects a destination that is too short rather than overrunning it', () {
      expect(() => decodeMuLaw(Uint8List(64), into: Int16List(32)), throwsA(isA<AssertionError>()));
    });
  });

  group('the fixture shared with the PC', () {
    // 512 bytes of a 440 Hz tone at 24 kHz, amplitude 20000, as the C# encoder produces them.
    const fixtureHex =
        'ffbdada59e9a9693908f8e8d8c8c8c8c8d8d8e8f9295999da2abb8d84330271f1b171411'
        '0f0e0d0c0c0c0c0c0d0e0f1114181c2028334acdb4a9a19c9895928f8e8d8c8c8c8c8c8d'
        '8e8f9194979b9fa6afc05e392c231d191612100e0d0d0c0c0c0c0d0d0e1013161a1e242c'
        '3b6abeaea59e9a9793908f8e8d8c8c8c8c8d8d8e8f9295999ca1aab6d14631281f1b1814'
        '110f0e0d0c0c0c0c0c0d0e0f1114181b1f283146d1b6aaa19c9995928f8e8d8d8c8c8c8c'
        '8d8e8f9093979a9ea5aebe6a3b2c241e1a1613100e0d0d0c0c0c0c0d0d0e101216191d23'
        '2c395ec0afa69f9b9794918f8e8d8c8c8c8c8c8d8e8f9295989ca1a9b4cd4a3328201c18'
        '14110f0e0d0c0c0c0c0c0d0e0f1114171b1f273043d8b8aba29d9995928f8e8d8d8c8c8c'
        '8c8d8e8f9093969a9ea5adbdff3c2d251e1a1613100f0e0d0c0c0c0c0d0d0e0f1215191d'
        '222b3858c3b0a79f9b9794918f8e8d8c8c8c8c8c8d8e8f9195989ca0a8b3ca4d3429211c'
        '1815120f0e0d0c0c0c0c0c0d0e0f1114171b1f262f40deb9aca39d999692908e8d8d8c8c'
        '8c8c8d8d8e9093969a9ea4acbbea3e2e251e1a1713100f0e0d0c0c0c0c0d0d0e0f121519'
        '1c212a3651c7b1a89f9b9894918f8e8d8c8c8c8c8c8d8e8f9194989b9fa8b1c751362a21'
        '1c1915120f0e0d0d0c0c0c0c0d0e0f1013171a1e252e3eeabbaca49e9a9693908e8d8d8c'
        '8c8c8c8d8d8e9092';

    final fixture = Uint8List.fromList(
      List<int>.generate(fixtureHex.length ~/ 2,
          (i) => int.parse(fixtureHex.substring(i * 2, i * 2 + 2), radix: 16)),
    );

    test('decodes to a 440 Hz tone, not to noise', () {
      final samples = decodeMuLaw(fixture);
      expect(samples.length, 512);

      // Zero crossings. A 440 Hz tone at 24 kHz crosses zero 880 times a second, so 512 samples
      // is about 18.8 crossings. A curve whose segment search is off by one still produces
      // "audio" with plausible loudness, so counting zero crossings is what actually tells the
      // two apart — an SNR check on a decode of a decode would just measure the same bug twice.
      var crossings = 0;
      for (var i = 1; i < samples.length; i++) {
        if ((samples[i - 1] < 0) != (samples[i] < 0)) crossings++;
      }
      expect(crossings, inInclusiveRange(14, 24), reason: '$crossings zero crossings in 512 samples');
    });

    test('the amplitude is in the right place on the curve', () {
      // A 20000-amplitude tone should decode to roughly that. Four times too large means the
      // decoder shifted an extra factor of two in; four times too small means it did not.
      final samples = decodeMuLaw(fixture);
      var peak = 0;
      for (final s in samples) {
        if (s.abs() > peak) peak = s.abs();
      }
      expect(peak, inInclusiveRange(16000, 24000));
    });

    test('it is symmetric, because the tone is', () {
      // Root-mean-square rather than peak. The fixture starts at phase zero, so where the
      // sampling happens to land relative to the positive and negative crests is not the same,
      // and comparing peaks measures the phase of the window rather than the decoder. Over
      // nine cycles the energies have to match whatever the phase.
      final samples = decodeMuLaw(fixture);
      double energy(bool positive) {
        var sum = 0;
        var n = 0;
        for (final s in samples) {
          if (positive ? s > 0 : s < 0) {
            sum += s * s;
            n++;
          }
        }
        return n == 0 ? 0 : sum / n;
      }

      final up = energy(true);
      final down = energy(false);
      expect(down, greaterThan(0));
      expect((up - down).abs() / up, lessThan(0.02));
    });
  });
}
