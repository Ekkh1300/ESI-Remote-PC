import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'machine_store.dart';

/// One persisted preference with one question behind it (D87): while the phone is carrying
/// the PC's sound, should the PC's own speakers be silenced?
///
/// The default is **true**, and that default is the feature: the phone as the PC's speaker
/// means the phone *is* the PC's speaker, the way a wireless one is - sound leaves the room's
/// other mouth once the phone is carrying it. The preference exists because the opposite
/// reading is legitimate (a machine whose speakers double as a room's PA), and because the
/// mechanism, on a device whose driver applies volume inside the audio engine, would silence
/// the loopback tap with the room. Both readings live behind the same toggle, and the phone's
/// silence meter is what tells the second reading's user why the phone went quiet.
///
/// Rides the same [FlutterSecureStorage] the device list rides, so the preference survives
/// reinstalls the way the pairing does, and tests drive it through the same in-memory
/// backing rather than the platform channel.
class SpeakerSettings {
  SpeakerSettings(this._storage);

  final FlutterSecureStorage _storage;

  factory SpeakerSettings.secure() =>
      SpeakerSettings(const FlutterSecureStorage());

  factory SpeakerSettings.inMemory([Map<String, String>? seed]) =>
      SpeakerSettings(InMemorySecureStorage(seed ?? <String, String>{}));

  static const _silenceLocalKey = 'speaker.silenceLocal';

  /// Reads the persisted preference; [fallback] answers when nothing was ever written.
  Future<bool> silenceLocal({bool fallback = true}) async {
    final raw = await _storage.read(key: _silenceLocalKey);
    if (raw == null) return fallback;
    return raw == 'true';
  }

  Future<void> setSilenceLocal(bool value) =>
      _storage.write(key: _silenceLocalKey, value: value ? 'true' : 'false');
}

/// Wired like [machineStoreProvider]'s pattern in state/connection.dart: the store is
/// constructed fresh from the platform's real backing, and a test overrides this provider
/// with an [InMemorySecureStorage]-backed instance.
final speakerSettingsProvider = Provider<SpeakerSettings>(
  (ref) => SpeakerSettings.secure(),
);

