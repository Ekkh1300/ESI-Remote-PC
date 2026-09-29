import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../net/esi_api.dart';
import '../protocol/esi_crypto.dart';
import '../protocol/protocol_constants.dart';

/// A PC this phone is paired with (or is trying to pair with).
class PairedMachine {
  const PairedMachine({
    required this.deviceId,
    required this.host,
    required this.port,
    required this.pins,
    required this.keyId,
    required this.masterSecret,
    required this.nickname,
    required this.macAddress,
    required this.pairedAt,
    required this.lastSeenAt,
    required this.agentVersion,
    required this.features,
  });

  final String deviceId;
  final String host;
  final int port;

  /// SPKI pins. Normally one; two during a rotation so a paired phone is not bricked when
  /// the PC regenerates its certificate.
  final List<String> pins;

  final int keyId;

  /// Null until pairing completes.
  final Uint8List? masterSecret;

  final String nickname;
  final String? macAddress;
  final DateTime? pairedAt;
  final DateTime? lastSeenAt;
  final String agentVersion;
  final Map<String, bool> features;

  bool get isPaired => masterSecret != null && masterSecret!.isNotEmpty;

  EsiEndpoint get endpoint => EsiEndpoint(host: host, port: port, pins: pins);

  String get displayAddress =>
      port == EsiProtocol.defaultHttpsPort ? host : '$host:$port';

  EsiIdentity get identity => EsiIdentity(
        deviceId: deviceId,
        keyId: keyId,
        masterSecret: masterSecret ?? Uint8List(0),
      );

  String get displayName => nickname.isNotEmpty ? nickname : deviceNameFallback;

  /// The agent reports its own name in the handshake, but a device id is always available
  /// and never empty, so it is the last resort.
  String get deviceNameFallback =>
      deviceId.isNotEmpty ? 'Agent ${deviceId.substring(0, deviceId.length.clamp(0, 8))}' : 'Agent';

  PairedMachine copyWith({
    String? host,
    int? port,
    List<String>? pins,
    int? keyId,
    Uint8List? masterSecret,
    String? nickname,
    String? macAddress,
    DateTime? lastSeenAt,
    String? agentVersion,
    Map<String, bool>? features,
  }) =>
      PairedMachine(
        deviceId: deviceId,
        host: host ?? this.host,
        port: port ?? this.port,
        pins: pins ?? this.pins,
        keyId: keyId ?? this.keyId,
        masterSecret: masterSecret ?? this.masterSecret,
        nickname: nickname ?? this.nickname,
        macAddress: macAddress ?? this.macAddress,
        pairedAt: pairedAt,
        lastSeenAt: lastSeenAt ?? this.lastSeenAt,
        agentVersion: agentVersion ?? this.agentVersion,
        features: features ?? this.features,
      );

  Map<String, dynamic> toJson() => <String, dynamic>{
        'deviceId': deviceId,
        'host': host,
        'port': port,
        'pins': pins,
        'keyId': keyId,
        'masterSecret': masterSecret == null ? null : EsiCrypto.base64(masterSecret!),
        'nickname': nickname,
        'macAddress': macAddress,
        'pairedAt': pairedAt?.millisecondsSinceEpoch,
        'lastSeenAt': lastSeenAt?.millisecondsSinceEpoch,
        'agentVersion': agentVersion,
        'features': features,
      };

  static PairedMachine fromJson(Map<String, dynamic> json) {
    final secretB64 = json['masterSecret'];
    return PairedMachine(
      deviceId: asStringSafe(json['deviceId']),
      host: asStringSafe(json['host']),
      port: json['port'] is int ? json['port']! as int : EsiProtocol.defaultHttpsPort,
      pins: (json['pins'] is List)
          ? (json['pins'] as List)
              .whereType<Object>()
              .map((e) => e.toString())
              .where((s) => s.isNotEmpty)
              .toList()
          : const <String>[],
      keyId: json['keyId'] is int ? json['keyId']! as int : 1,
      masterSecret: secretB64 is String && secretB64.isNotEmpty
          ? _tryDecode(secretB64)
          : null,
      nickname: asStringSafe(json['nickname']),
      macAddress: json['macAddress'] is String ? json['macAddress'] as String : null,
      pairedAt: _date(json['pairedAt']),
      lastSeenAt: _date(json['lastSeenAt']),
      agentVersion: asStringSafe(json['agentVersion']),
      features: (json['features'] is Map)
          ? (json['features'] as Map).map((k, v) => MapEntry(k.toString(), v == true))
          : const <String, bool>{},
    );
  }

  static String asStringSafe(Object? value) => value is String ? value : '';

  static DateTime? _date(Object? millis) =>
      millis is int ? DateTime.fromMillisecondsSinceEpoch(millis) : null;

  static Uint8List? _tryDecode(String value) {
    try {
      return base64Decode(value);
    } on FormatException {
      return null;
    }
  }
}

/// Persists the machine list and the master secrets.
///
/// The list itself is not secret, but the master secret is the whole of this phone's
/// identity to the agent, so everything lives in [FlutterSecureStorage] rather than in
/// plain preferences. That also means a rooted-device attacker has to break the keystore
/// to impersonate the phone.
class MachineStore {
  MachineStore(this._storage);

  final FlutterSecureStorage _storage;

  static const String _indexKey = 'esi.machines.index';
  static const String _keyPrefix = 'esi.machine.';
  static const String _activeKey = 'esi.machines.active';

  /// The keystore-backed store used in production. `flutter_secure_storage` 11.x defaults
  /// to AES-GCM data encryption with RSA-OAEP key wrapping in the Android keystore, which
  /// is what a 32-byte master secret deserves.
  factory MachineStore.secure() => MachineStore(const FlutterSecureStorage());

  /// An in-memory store, used by tests and by the widget tree in `flutter test` where no
  /// platform channel exists.
  factory MachineStore.inMemory([Map<String, String>? seed]) =>
      MachineStore(_InMemorySecureStorage(seed ?? <String, String>{}));

  Future<List<PairedMachine>> loadAll() async {
    final raw = await _storage.read(key: _indexKey);
    if (raw == null || raw.isEmpty) return <PairedMachine>[];
    try {
      final decoded = jsonDecode(raw);
      if (decoded is! List) return <PairedMachine>[];
      return decoded
          .whereType<Object>()
          .map((e) => PairedMachine.fromJson(
                e is Map ? e.cast<String, dynamic>() : <String, dynamic>{},
              ))
          .where((m) => m.host.isNotEmpty)
          .toList();
    } on FormatException {
      return <PairedMachine>[];
    }
  }

  Future<void> saveAll(List<PairedMachine> machines) async {
    await _writeIndex(machines);
    for (final machine in machines) {
      await _storage.write(
        key: '$_keyPrefix${machine.deviceId.isEmpty ? machine.host : machine.deviceId}',
        value: jsonEncode(machine.toJson()),
      );
    }
  }

  Future<void> upsert(PairedMachine machine) async {
    final all = await loadAll();
    final index = all.indexWhere((m) => _sameAgent(m, machine));
    if (index >= 0) {
      all[index] = machine;
    } else {
      all.add(machine);
    }
    await saveAll(all);
  }

  Future<void> remove(String deviceId) async {
    final all = await loadAll();
    all.removeWhere((m) => m.deviceId == deviceId || (deviceId.isEmpty && m.host.isEmpty));
    await saveAll(all);
    await _storage.delete(key: '$_keyPrefix$deviceId');
  }

  Future<String?> readActiveDeviceId() => _storage.read(key: _activeKey);

  Future<void> writeActiveDeviceId(String deviceId) =>
      _storage.write(key: _activeKey, value: deviceId);

  Future<void> _writeIndex(List<PairedMachine> machines) => _storage.write(
        key: _indexKey,
        value: jsonEncode(<Map<String, dynamic>>[
          for (final machine in machines)
            <String, dynamic>{
              'deviceId': machine.deviceId,
              'host': machine.host,
              'port': machine.port,
            },
        ]),
      );

  static bool _sameAgent(PairedMachine a, PairedMachine b) =>
      (a.deviceId.isNotEmpty && a.deviceId == b.deviceId) ||
      (a.host == b.host && a.port == b.port);
}

/// [FlutterSecureStorage] with the same surface, backed by a map. Lets the whole app run
/// under `flutter test`, where no platform channel exists.
class _InMemorySecureStorage implements FlutterSecureStorage {
  _InMemorySecureStorage(this._values);

  final Map<String, String> _values;

  @override
  Future<String?> read({
    required String key,
    AppleOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    AppleOptions? mOptions,
    WindowsOptions? wOptions,
  }) async =>
      _values[key];

  @override
  Future<void> write({
    required String key,
    required String? value,
    AppleOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    AppleOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    if (value == null) {
      _values.remove(key);
    } else {
      _values[key] = value;
    }
  }

  @override
  Future<void> delete({
    required String key,
    AppleOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    AppleOptions? mOptions,
    WindowsOptions? wOptions,
  }) async =>
      _values.remove(key);

  @override
  Future<void> deleteAll({
    AppleOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    AppleOptions? mOptions,
    WindowsOptions? wOptions,
  }) async =>
      _values.clear();

  @override
  Future<bool> containsKey({
    required String key,
    AppleOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    AppleOptions? mOptions,
    WindowsOptions? wOptions,
  }) async =>
      _values.containsKey(key);

  @override
  Future<Map<String, String>> readAll({
    AppleOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    AppleOptions? mOptions,
    WindowsOptions? wOptions,
  }) async =>
      Map<String, String>.from(_values);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
