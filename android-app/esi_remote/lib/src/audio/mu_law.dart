/// G.711 µ-law, the companding curve the PC's audio is encoded with.
///
/// This is the second implementation of this curve in the project; the other is
/// `windows-agent/src/ESI.RemotePc.Platform/Audio/MuLaw.cs`. The two are pinned to one
/// fixture by `test/audio/mu_law_test.dart` and `MuLawTests`, because two implementations of
/// one specification in two languages can each be perfectly self-consistent and still disagree
/// with each other — and neither suite would notice, since each only ever decodes what it
/// encoded.
///
/// Opus would be the better codec and is not available: NAudio ships no Opus encoder, Flutter
/// has no decoder for it here, and the repository has no native build at all. See the C# side
/// for the full reasoning. µ-law is a memoryless curve — one byte per sample, nothing carried
/// over — which is why two independent implementations can be made to agree exactly.
library;

import 'dart:typed_data';

/// Expands µ-law bytes to 16-bit samples.
///
/// Writes into [into] at [offset] when given, which is how the streaming path avoids allocating
/// a fresh list per packet — at fifty packets a second that is the difference between a steady
/// heap and a sawtooth one. Returns [into] in that case, and a new list otherwise.
///
/// The result is 16-bit, not 14-bit. The curve is *defined* over 14 bits and its bias is 132
/// rather than the 33 the encoder adds, precisely because this side works in the 16-bit domain
/// throughout. Shifting the result left by two scales the whole signal by four: audio of the
/// right shape at the wrong pitch.
Int16List decodeMuLaw(Uint8List source, {Int16List? into, int offset = 0}) {
  final out = into ?? Int16List(source.length);
  assert(
    offset >= 0 && offset + source.length <= out.length,
    'the destination is too short: $offset + ${source.length} > ${out.length}',
  );
  for (var i = 0; i < source.length; i++) {
    out[offset + i] = decodeOne(source[i]);
  }
  return out;
}

/// Expands one µ-law byte.
///
/// The stored code is the one's complement of the curve, and the high bit is the inverted sign,
/// so the first thing this does is invert — which puts both the sign bit and the segment back
/// where the shifts below expect them.
int decodeOne(int code) {
  final inverted = (~code) & 0xFF;

  // The mantissa gives four bits within the segment, the shift count gives the segment. The
  // bias is added before the shift so that it is scaled along with everything else, which is
  // why it comes back off after.
  var magnitude = (((inverted & 0x0F) << 3) + _bias) << ((inverted & 0x70) >> 4);
  magnitude -= _bias;

  final value = (inverted & 0x80) != 0 ? -magnitude : magnitude;

  // Every one of the 256 codes has to land in range. The largest shift is seven and the
  // largest mantissa path reaches 32124, so the clamp is a formality — but it is a formality
  // that runs on every sample, and a phone that clips a thousand times a second is a different
  // bug from one that clips in a test.
  if (value > 32767) return 32767;
  if (value < -32768) return -32768;
  return value;
}

const int _bias = 0x84; // 132 in the 16-bit domain; the PC side adds 33 before halving.
