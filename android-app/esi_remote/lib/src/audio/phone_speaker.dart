import 'dart:async';

import 'package:flutter/foundation.dart';
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
///
/// What one speaker session tells the media screen about itself. Built whenever the
/// speaker emits [PhoneSpeaker.stats] and read as a whole: the panel shows the counters
/// and the two failure kinds the counters can say - "the PC is sending pure silence"
/// and "the stream has stopped arriving".
///
/// Silent is derived on the receive loop, not recomputed by the panel: the raw samples
/// are here and only here. A packet counts silent when its decoded peak stays under
/// [silencePeak], which sits a belt and braces under the dither noise the Windows
/// mixer always supplies but still under the quietest real content. [silentStreak]
/// counts consecutive such packets from the end of the stream, so one loud sample
/// anywhere clears it and silence must build again.
class SpeakerStats {
  const SpeakerStats({
    required this.running,
    this.packets = 0,
    this.dropped = 0,
    this.silentStreak = 0,
    this.sampleRate,
    this.channels,
    this.lastPacketAt,
  });

  /// Whether the speaker is declared running (the switch is on and it believes it).
  final bool running;

  /// Packets decoded and handed to the track this session.
  final int packets;

  /// Packets dropped rather than played, mostly around a format change.
  final int dropped;

  /// Consecutive silent packets ending at the newest packet.
  final int silentStreak;

  /// The format last announced by the PC. Null until the first packet arrives.
  final int? sampleRate;

  final int? channels;

  /// When the newest packet arrived. A stream that stopped arriving - the socket died,
  /// or the PC stopped capturing with the switch still on - shows as a [lastPacketAt]
  /// that keeps getting older while nothing else changes.
  final DateTime? lastPacketAt;

  /// Above this decoded peak (1 of 32768, about -42 dBFS) a packet is not silence.
  static const int silencePeak = 256;

  /// Consecutive silent packets at roughly 51 a second before the UI says "silence".
  /// A hair over two seconds: shorter, and the gap between video cuts would flash it.
  static const int silentPacketStreak = 32;

  /// True once the stream is running, has been going long enough to mean it, and the
  /// last [silentPacketStreak] packets have all been silence. "Nothing is playing on
  /// the PC right now" - the loud section that may have played earlier does not count
  /// against it, because a video that ended several seconds ago and left the phone
  /// listening to mixer dither is exactly the case the user is staring at.
  bool get saysSilence =>
      running && packets >= silentPacketStreak && silentStreak >= silentPacketStreak;
}

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

  /// Consecutive silent packets ending at the newest one. Reset by any packet with a peak
  /// at or above [SpeakerStats.silencePeak]; grown by every packet under it.
  int _silentStreak = 0;

  /// When the newest packet arrived, for the "the stream stopped arriving" state.
  DateTime? _lastPacketAt;

  /// Says what this session has done, recomputed at most every [statsInterval]. A
  /// [ValueNotifier] rather than a Riverpod stream, because the updates ride the decode
  /// loop - over fifty a second - and a panel that rebuilt on every one would spend its
  /// frame budget showing a number that changes fifty times a second.
  final ValueNotifier<SpeakerStats> stats =
      ValueNotifier<SpeakerStats>(const SpeakerStats(running: false));

  /// The floor on how often [stats] is allowed to gain a new value.
  static const Duration statsInterval = Duration(milliseconds: 400);

  /// When the newest accepted update was built, to rate-limit publish calls that ride
  /// the decode loop.
  DateTime _lastStatsAt = DateTime.fromMillisecondsSinceEpoch(0);

  /// Publishes the current session statistics, at most [statsInterval] apart, unless
  /// [force] is given - stop and start want to be exact, even when packets are silent
  /// and nothing else is queued behind the throttle.
  void _publishStats({bool force = false}) {
    final now = DateTime.now();
    if (!force && _running && now.difference(_lastStatsAt) < statsInterval) return;
    _lastStatsAt = now;
    stats.value = SpeakerStats(
      running: _running,
      packets: _packetsPlayed,
      dropped: _dropped,
      silentStreak: _silentStreak,
      sampleRate: _sampleRate,
      channels: _channels,
      lastPacketAt: _lastPacketAt,
    );
  }

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
    _publishStats(force: true);
  }

  /// Stops playing and tells the PC to stop capturing.
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
    _silentStreak = 0;
    // Anything the panel showed about this session is this session's, not the next one's:
    // stale "the stream stopped arriving" would hang over a fresh start until its first
    // tick, saying the exact opposite of what just started.
    _lastPacketAt = null;
    try {
      await channel.invokeMethod<void>('stop');
    } on PlatformException {
      // The track was already gone - the activity was finishing, or the engine had torn the
      // channel down. Nothing to clean up on either side.
    } on MissingPluginException {
      // The host half of the channel does not exist on this platform at all - a unit test
      // running the state machine, or any build without the MainActivity host. The call has
      // nowhere to arrive, which is the same "nothing to clean up" answer with a different
      // cause: the notifier's stop path runs in both worlds, so swallowing the one channel
      // answer that means "there is no device end" is what keeps stop idempotent where
      // there is no device.
    }
    if (onStopped != null) await onStopped();
    _publishStats(force: true);
  }

  void _onFrame(Uint8List frame) {
    if (!_running) return;

    final chunk = parseAudioChunk(frame);
    if (chunk == null) return; // Not an audio frame, or a truncated one.

    _lastPacketAt = DateTime.now();

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
    // Preliminary silence classification, computed where the samples already are rather
    // than re-decoded for a meter. Any packet the loop cannot classify (the empty packet)
    // stays in the streak it was in; a lost packet is not evidence of sound.
    var peak = 0;
    for (var i = 0; i < pcm.length; i++) {
      final magnitude = pcm[i] < 0 ? -pcm[i] : pcm[i];
      if (magnitude > peak) peak = magnitude;
    }
    if (pcm.isNotEmpty && peak >= SpeakerStats.silencePeak) {
      _silentStreak = 0;
    } else {
      _silentStreak++;
    }

    // `Int16List` is native-endian and Android is little-endian, so the buffer can go straight
    // across. The explicit view is still used so the intent survives a future port to a
    // big-endian target rather than silently swapping the bytes of every sample.
    final bytes = ByteData.sublistView(pcm).buffer.asUint8List();
    channel.invokeMethod<void>('write', bytes);
    _packetsPlayed++;
    _publishStats();
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
