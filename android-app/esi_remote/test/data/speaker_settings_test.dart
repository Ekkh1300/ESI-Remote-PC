import 'package:esi_remote/src/data/speaker_settings.dart';
import 'package:flutter_test/flutter_test.dart';

/// The D87 preference store: one question, one key, one default that IS the feature.
///
/// The default is what a phone that has never been asked answers. Everything else here is
/// round-tripping: the store must answer tomorrow what the user said today, when the app
/// (and the battery, and the capture with it) is long gone between the saying and the
/// asking.
void main() {
  group('SpeakerSettings', () {
    test('a store that was never written answers the default - silencing on', () async {
      final store = SpeakerSettings.inMemory();

      expect(await store.silenceLocal(), isTrue);
    });

    test('what was written is what comes back', () async {
      final store = SpeakerSettings.inMemory();

      await store.setSilenceLocal(false);
      expect(await store.silenceLocal(), isFalse);

      await store.setSilenceLocal(true);
      expect(await store.silenceLocal(), isTrue);
    });

    test('a write survives a re-open of the store, like a reinstall does on a device', () async {
      final backing = <String, String>{};
      final first = SpeakerSettings.inMemory(backing);
      await first.setSilenceLocal(false);

      // A brand-new instance over the same backing: the launch after the app was killed.
      final second = SpeakerSettings.inMemory(backing);
      expect(await second.silenceLocal(), isFalse);
    });

    test('anything that is not exactly `true` reads as false, never as the default', () async {
      // A corrupted or hand-edited value must fail closed toward "do not silence" - the
      // reading where the room keeps its mouth open - rather than inherit the default.
      final store = SpeakerSettings.inMemory(<String, String>{
        'speaker.silenceLocal': 'yes',
      });

      expect(await store.silenceLocal(), isFalse);
    });
  });
}
