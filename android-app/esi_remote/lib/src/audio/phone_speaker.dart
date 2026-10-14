import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../net/esi_ws.dart';
import 'mu_law.dart';

/// Plays the PC's own audio on this phone - the phone as the PC's speaker.
///
/// The PC captures its default output device (WASAPI loopback), µ-law encodes it, and pushes
/// it down the same socket the screen arrives on. This class is the other end: it picks the
/// audio frames out of that socket, expands them back to 16-bit PCM, and hands the samples to
/// a native [AudioTrack] through [channel].
///
/// Why the samples cross a platform channel rather than being played by a Dart plugin: this
/// repository has no native build of its own, and adding one for a single playback path would
/// drag an NDK and a CMake toolchain into a build that deliberately avoids them (see the
/// `path_provider_android` note in `pubspec.yaml`). A [MethodChannel] host in `MainActivity.kt`
/// plays the same bytes with none of that, and the µ-law curve - the part worth testing - stays
/// in Dart where it is already pinned to the C# implementation by a shared fixture.
class PhoneSpeaker {
  PhoneSpeaker({required this.channel});

  /// The channel the native `AudioTrack` host answers on.
  ///
  /// Overridable so tests can assert on the byte stream without an Android engine.
  final MethodChannel channel;

  StreamSubscription<Uint8List>? _frames;
  bool _running = false;

  /// Whether this speaker is currently asking the PC for audio and playing it.
  bool get isRunning => _running;

  /// Decoded packets, for a meter or a test. Not used by the UI yet.
  int _packetsPlayed = 0;
  int get packetsPlayed => _packetsPlayed;

  /// Packets thrown away rather than played - the one that arrives before a reopened track is
  /// ready. Counted rather than hidden because a phone that silently skips audio on every
  /// format change sounds exactly like one with a codec bug.
  int _dropped = 0;
  int get dropped => _dropped;

  /// The format last announced by the PC, so a restart after a device change does not keep
  /// an AudioTrack opened at a rate the new stream no longer uses.
  ///
  /// Null rather than zero until the first frame arrives. Zero would be indistinguishable from
  /// a real format, and the first packet of every session would take the reopen path - which
  /// stops a track that does not exist yet and then discards that packet, so the session would
  /// open with a hole in it for no reason at all.
  int? _sampleRate;
  int? _channels;

  /// The payload of the frame that triggered an open, held across the await.
  Uint8List? _pendingMuLaw;

  /// Starts listening to [source] and playing every audio frame it carries.
  ///
  /// Returns once the native track is open. A second call while running is a no-op, matching
  /// `AudioPump.Start` on the PC side: the caller's intent is "audio on", and re-opening a
  /// device that is already open would only drop the samples already queued.
  Future<void> start(Stream<Uint8List> source) async {
    if (_running) return;
    _frames = source.listen(_onFrame);
    _running = true;
    // The rate and channel count are not known until the first frame arrives, so the track is
    // opened lazily in [_open] - see the comment on [_sampleRate] for why it is not opened here.
  }

  /// Stops playing and asks the PC to stop capturing.
  ///
  /// Takes the socket's stop callback so this class never has to hold the socket itself -
  /// it is handed the byte stream and told how to stop, which is what makes it testable
  /// without a connection.
  Future<void> stop({Future<void> Function()? onStopped}) async {
    if (!_running) return;
    _running = false;
    await _frames?.cancel();
    _frames = null;
    // The format is forgotten along with the track. A later start must open a fresh track at
    // whatever the PC announces then, not assume the one just closed is still the right shape.
    _sampleRate = null;
    _channels = null;
    _pendingMuLaw = null;
    try {
      await channel.invokeMethod<void>('stop');
    } on PlatformException {
      // The track was already gone - the activity was finishing, or the engine had torn the
      // channel down. Nothing to clean up on either side.
    }
    if (onStopped != null) await onStopped();
  }

  void _onFrame(Uint8List frame) {
    if (!_running) return;

    final chunk = parseAudioChunk(frame);
    if (chunk == null) return; // Not an audio frame, or a truncated one.

    // The PC always sends 24 kHz stereo. If it ever does not, the AudioTrack has to be
    // reopened at the new format rather than fed samples at the old rate - which would come
    // out as the right sound at the wrong pitch, a bug that looks like a codec fault.
    //
    // The very first frame is not a change: there is no track to reopen, and discarding the
    // packet that tells us the format would put a hole at the start of every session.
    final first = _sampleRate == null;
    if (first || _sampleRate != chunk.sampleRate || _channels != chunk.channels) {
      // Held across the open, because the payload belongs to the frame that triggered it and
      // [_open] has to await the platform before it can be written.
      _pendingMuLaw = chunk.muLaw;
      unawaited(_open(chunk.sampleRate, chunk.channels, keepThisPacket: first));
      return;
    }

    _write(chunk.muLaw);
  }

  /// Opens the track at [sampleRate]/[channels].
  ///
  /// [keepThisPacket] is true only for the first frame of a session: nothing is lost by
  /// playing it, because the track is being opened *for* it. On a mid-stream change the
  /// triggering packet is dropped instead - it arrived before the new track was ready, and
  /// holding it would put one packet of stale-format audio in front of the live stream.
  Future<void> _open(int sampleRate, int channels, {required bool keepThisPacket}) async {
    if (_sampleRate != null) {
      try {
        await channel.invokeMethod<void>('stop');
      } on PlatformException {
        // No track to stop; the one below is opened fresh either way.
      }
    }
    _sampleRate = sampleRate;
    _channels = channels;
    await channel.invokeMethod<void>('start', <String, dynamic>{
      'sampleRate': sampleRate,
      'channels': channels,
    });

    if (keepThisPacket) {
      final pending = _pendingMuLaw;
      _pendingMuLaw = null;
      if (pending != null) _write(pending);
    } else {
      _pendingMuLaw = null;
      _dropped++;
    }
  }

  void _write(Uint8List muLaw) {
    final pcm = decodeMuLaw(muLaw);
    // `Int16List` is native-endian and Android is little-endian, so the buffer can go straight
    // across. The explicit view is still used so the intent survives a future port to a
    // big-endian target rather than silently swapping the bytes of every sample.
    final bytes = ByteData.sublistView(pcm).buffer.asUint8List();
    channel.invokeMethod<void>('write', bytes);
    _packetsPlayed++;
  }
}

/// Riverpod provider so the media screen can drive the speaker without holding it directly.
///
/// Not autoDispose: the speaker has native state that outlives a widget rebuild, and a
/// provider that disposed on the last listener leaving would cut the audio mid-sentence when
/// the user navigates to another screen.
final phoneSpeakerProvider = Provider<PhoneSpeaker>(
  (ref) => PhoneSpeaker(channel: const MethodChannel('esi_remote/audio')),
);
