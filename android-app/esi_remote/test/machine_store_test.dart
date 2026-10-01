/// Tests for the machine store, which is where a successful pairing either survives or is
/// quietly lost.
///
/// The failure these cover is not hypothetical: the index stores only a pointer to each
/// machine, and the master secret lives in the record that pointer names. Reading the list
/// back from the index therefore returned every machine with no secret, and every screen
/// said "Pairing did not finish" no matter how well the handshake had gone.
library;

import 'dart:convert';
import 'dart:typed_data';

import 'package:esi_remote/src/data/machine_store.dart';
import 'package:esi_remote/src/protocol/protocol_constants.dart';
import 'package:flutter_test/flutter_test.dart';

PairedMachine _machine({
  String deviceId = 'da704eb78efb0a3f',
  String host = '192.168.1.42',
  String nickname = 'E-K',
  Uint8List? secret,
}) =>
    PairedMachine(
      deviceId: deviceId,
      host: host,
      port: EsiProtocol.defaultHttpsPort,
      pins: const <String>['ahwxjI1mzdkuLgYF4+P239b5oFBVBswJlQh6K2galow='],
      keyId: 1,
      masterSecret: secret,
      nickname: nickname,
      macAddress: null,
      pairedAt: DateTime.utc(2026, 10, 1),
      lastSeenAt: null,
      agentVersion: '1.0.0',
      features: const <String, bool>{},
    );

void main() {
  group('MachineStore', () {
    test('a paired machine comes back with its master secret', () async {
      final store = MachineStore.inMemory();
      final secret = Uint8List.fromList(List<int>.generate(32, (i) => i + 1));

      await store.upsert(_machine(secret: secret));

      final all = await store.loadAll();
      expect(all, hasLength(1));
      expect(all.first.deviceId, 'da704eb78efb0a3f');
      expect(all.first.host, '192.168.1.42');
      expect(all.first.nickname, 'E-K');
      expect(all.first.masterSecret, secret);
      expect(all.first.isPaired, isTrue, reason: 'a stored master secret means paired');
    });

    test('the index never carries the secret; only the record does', () async {
      final backing = <String, String>{};
      final store = MachineStore.inMemory(backing);

      await store.upsert(_machine(
        secret: Uint8List.fromList(List<int>.filled(32, 0xAA)),
      ));

      // The index is a pointer list. The secret is this phone's whole identity to the
      // agent, so it must not be duplicated into it.
      final index = jsonDecode(backing['esi.machines.index']!);
      expect(index, isA<List<dynamic>>());
      expect((index as List<dynamic>).single.toString(), isNot(contains('masterSecret')));

      // The record named by the index is what carries it.
      final record = jsonDecode(backing['esi.machine.da704eb78efb0a3f']!);
      expect((record as Map<String, dynamic>)['masterSecret'], isNotEmpty);
    });

    test('an index entry whose record has gone still yields a usable machine', () async {
      final backing = <String, String>{};
      final store = MachineStore.inMemory(backing);
      await store.upsert(_machine());
      backing.remove('esi.machine.da704eb78efb0a3f');

      final all = await store.loadAll();
      expect(all, hasLength(1));
      expect(all.first.deviceId, 'da704eb78efb0a3f');
      expect(all.first.isPaired, isFalse);
    });

    test('a machine without a secret is not treated as paired', () async {
      final store = MachineStore.inMemory();
      await store.upsert(_machine());

      final all = await store.loadAll();
      expect(all, hasLength(1));
      expect(all.first.masterSecret, isNull);
      expect(all.first.isPaired, isFalse);
    });

    test('two PCs are kept apart, and both keep their secrets', () async {
      final store = MachineStore.inMemory();
      final first = Uint8List.fromList(List<int>.filled(32, 0x11));
      final second = Uint8List.fromList(List<int>.filled(32, 0x22));

      await store.upsert(_machine(deviceId: 'aaa', host: '192.168.1.10', secret: first));
      await store.upsert(_machine(deviceId: 'bbb', host: '192.168.1.11', secret: second));

      final all = await store.loadAll();
      expect(all, hasLength(2));
      expect(all.map((m) => m.deviceId).toSet(), <String>{'aaa', 'bbb'});
      expect(all.firstWhere((m) => m.deviceId == 'aaa').masterSecret, first);
      expect(all.firstWhere((m) => m.deviceId == 'bbb').masterSecret, second);
    });

    test('re-pairing the same PC replaces the record rather than adding one', () async {
      final store = MachineStore.inMemory();

      await store.upsert(_machine(secret: Uint8List.fromList(List<int>.filled(32, 1))));
      await store.upsert(_machine(secret: Uint8List.fromList(List<int>.filled(32, 2))));

      final all = await store.loadAll();
      expect(all, hasLength(1));
      expect(all.single.masterSecret, Uint8List.fromList(List<int>.filled(32, 2)));
    });

    test('remove takes the record with it', () async {
      final store = MachineStore.inMemory();
      await store.upsert(_machine(secret: Uint8List.fromList(List<int>.filled(32, 7))));

      await store.remove('da704eb78efb0a3f');

      expect(await store.loadAll(), isEmpty);
    });

    test('the active device id round-trips', () async {
      final store = MachineStore.inMemory();
      expect(await store.readActiveDeviceId(), isNull);

      await store.writeActiveDeviceId('da704eb78efb0a3f');
      expect(await store.readActiveDeviceId(), 'da704eb78efb0a3f');
    });
  });
}