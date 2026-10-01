import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../data/machine_store.dart';
import '../data/pairing_service.dart';
import '../net/esi_api.dart';
import '../net/esi_ws.dart';
import '../protocol/models.dart';
import '../protocol/protocol_constants.dart';

/// Overridable in tests. Production uses the platform keystore.
final machineStoreProvider = Provider<MachineStore>((ref) => MachineStore.secure());

final pairingServiceProvider = Provider<PairingService>((ref) => PairingService());

/// Every machine this phone knows about, paired or pending.
class MachineListNotifier extends Notifier<List<PairedMachine>> {
  bool _loaded = false;

  @override
  List<PairedMachine> build() {
    if (!_loaded) {
      _loaded = true;
      unawaited(_load());
    }
    return const <PairedMachine>[];
  }

  Future<void> _load() async {
    final stored = await ref.read(machineStoreProvider).loadAll();
    state = stored;
  }

  Future<void> reload() => _load();

  Future<void> add(PairedMachine machine) async {
    await ref.read(machineStoreProvider).upsert(machine);
    await _load();
  }

  Future<void> update(PairedMachine machine) => add(machine);

  Future<void> remove(String deviceId) async {
    await ref.read(machineStoreProvider).remove(deviceId);
    final active = ref.read(activeDeviceIdProvider);
    if (active == deviceId) {
      await ref.read(machineStoreProvider).writeActiveDeviceId('');
    }
    await _load();
  }

  PairedMachine? byDeviceId(String deviceId) {
    for (final machine in state) {
      if (machine.deviceId == deviceId) return machine;
    }
    return null;
  }
}

final machineListProvider =
    NotifierProvider<MachineListNotifier, List<PairedMachine>>(MachineListNotifier.new);

/// Which machine the app is currently pointed at.
class ActiveDeviceNotifier extends Notifier<String> {
  @override
  String build() {
    final machines = ref.watch(machineListProvider);
    final stored = _storedDeviceId;
    if (stored != null && stored.isNotEmpty) {
      for (final machine in machines) {
        if (machine.deviceId == stored) return stored;
      }
    }
    // Fall back to the only paired machine, which is the common single-PC case.
    for (final machine in machines) {
      if (machine.isPaired) return machine.deviceId;
    }
    return stored ?? '';
  }

  String? _storedDeviceId;

  /// Called once at startup so [build] can prefer the machine the user last chose.
  Future<void> initialise() async {
    _storedDeviceId = await ref.read(machineStoreProvider).readActiveDeviceId();
    state = build();
  }

  Future<void> select(String deviceId) async {
    _storedDeviceId = deviceId;
    await ref.read(machineStoreProvider).writeActiveDeviceId(deviceId);
    state = deviceId;
  }
}

final activeDeviceIdProvider =
    NotifierProvider<ActiveDeviceNotifier, String>(ActiveDeviceNotifier.new);

/// The machine the whole app is talking to, or null when nothing is selected/paired.
final activeMachineProvider = Provider<PairedMachine?>((ref) {
  final deviceId = ref.watch(activeDeviceIdProvider);
  final machines = ref.watch(machineListProvider);
  if (deviceId.isNotEmpty) {
    for (final machine in machines) {
      if (machine.deviceId == deviceId) return machine;
    }
  }
  for (final machine in machines) {
    if (machine.isPaired) return machine;
  }
  return null;
});

/// The signed REST client for the active machine, rebuilt whenever the machine changes.
final apiProvider = Provider<EsiApi?>((ref) {
  final machine = ref.watch(activeMachineProvider);
  if (machine == null || !machine.isPaired) return null;

  final api = EsiApi(endpoint: machine.endpoint, identity: machine.identity);
  ref.onDispose(api.close);
  return api;
});

/// The live socket for the active machine.
final webSocketProvider = Provider<EsiWebSocket?>((ref) {
  final machine = ref.watch(activeMachineProvider);
  if (machine == null || !machine.isPaired) return null;

  final socket = EsiWebSocket(endpoint: machine.endpoint, identity: machine.identity);
  ref.onDispose(() {
    unawaited(socket.dispose());
  });
  return socket;
});

/// A snapshot of how the app is connected to the active machine, including the last
/// failure in a form the UI can explain.
class ConnectionStatus {
  const ConnectionStatus({
    this.handshake,
    this.whoAmI,
    this.connectedAt,
    this.error,
    this.errorDetail,
    this.busy = false,
    this.socketState = EsiSocketState.idle,
  });

  final Handshake? handshake;
  final WhoAmI? whoAmI;
  final DateTime? connectedAt;
  final String? error;
  final String? errorDetail;
  final bool busy;
  final EsiSocketState socketState;

  bool get isOnline => handshake != null && error == null;

  Set<EsiScope> get scopes => whoAmI?.scopeSet ?? const <EsiScope>{};

  /// A feature the agent did not advertise is greyed out rather than shown as broken.
  bool supports(String feature) => handshake?.supports(feature) ?? false;

  bool hasScope(EsiScope scope) => scopes.isEmpty || scopes.contains(scope);

  ConnectionStatus copyWith({
    Handshake? handshake,
    WhoAmI? whoAmI,
    DateTime? connectedAt,
    String? error,
    String? errorDetail,
    bool? busy,
    EsiSocketState? socketState,
    bool clearError = false,
  }) =>
      ConnectionStatus(
        handshake: handshake ?? this.handshake,
        whoAmI: whoAmI ?? this.whoAmI,
        connectedAt: connectedAt ?? this.connectedAt,
        error: clearError ? null : (error ?? this.error),
        errorDetail: clearError ? null : (errorDetail ?? this.errorDetail),
        busy: busy ?? this.busy,
        socketState: socketState ?? this.socketState,
      );
}

/// Owns the connection lifecycle: handshake, identity check, socket supervision, and the
/// human-readable form of whatever went wrong.
class ConnectionNotifier extends Notifier<ConnectionStatus> {
  StreamSubscription<EsiSocketState>? _socketSubscription;

  @override
  ConnectionStatus build() {
    final machine = ref.watch(activeMachineProvider);
    final socket = ref.watch(webSocketProvider);

    if (machine == null || !machine.isPaired) {
      return const ConnectionStatus();
    }

    // A key rotation replaces the machine record, so the API client has to be rebuilt
    // with the new identity before anything else is signed.
    final api = ref.read(apiProvider);
    if (api != null && api.identity.keyId != machine.keyId) {
      api.identity = machine.identity;
    }
    if (socket != null && socket.identity.keyId != machine.keyId) {
      socket.identity = machine.identity;
      unawaited(socket.recycle());
    }

    // Riverpod does not give a Notifier its `state` until build() has returned, so neither
    // of these may run inline: `_attachSocket` reads `state.socketState`, `_refresh` reads
    // `state.busy`, and both assign to `state`. Doing it here threw "Tried to read the state
    // of an uninitialized provider", which put the whole shell on a blank screen for every
    // paired phone. One turn later the state exists.
    var alive = true;
    ref.onDispose(() {
      alive = false;
      unawaited(_socketSubscription?.cancel());
    });

    Future<void>.microtask(() {
      if (!alive) return;
      _attachSocket(socket);
      unawaited(_refresh(machine));
    });

    return const ConnectionStatus();
  }

  void _attachSocket(EsiWebSocket? socket) {
    if (socket == null) {
      return;
    }
    _socketSubscription?.cancel();
    _socketSubscription = socket.stateChanges.listen((next) {
      if (next == EsiSocketState.live) {
        unawaited(_loadWhoAmI());
      }
      state = state.copyWith(socketState: next, busy: false);
    });
    if (state.socketState != socket.state) {
      state = state.copyWith(socketState: socket.state);
    }
    if (socket.state == EsiSocketState.idle) {
      unawaited(socket.connect());
    }
  }

  Future<void> _refresh(PairedMachine machine) async {
    if (state.busy) return;
    state = state.copyWith(busy: true, clearError: true);

    final api = ref.read(apiProvider);
    if (api == null) {
      state = state.copyWith(busy: false);
      return;
    }

    // The handshake is unauthenticated and pinned with the QR's pin, which is exactly the
    // check that has to pass before anything signed is attempted.
    final handshake = await readHandshakeUnpinned(machine.endpoint);
    if (handshake == null) {
      state = state.copyWith(
        busy: false,
        error: 'No answer from ${machine.displayAddress}.',
        errorDetail:
            'The phone could not complete a TLS handshake with the agent. Either the PC '
            'is off or asleep, the address is wrong, or the agent is using a different '
            'port.',
      );
      return;
    }

    if (!handshake.isCompatible) {
      state = state.copyWith(
        busy: false,
        handshake: handshake,
        error: 'Protocol version ${handshake.protocolVersion} is not supported.',
        errorDetail:
            'This app speaks ESI protocol v${EsiProtocol.version}. Update whichever side '
            'is older; the two have to be upgraded together because the request-signing '
            'scheme is part of the protocol version.',
      );
      return;
    }

    // A pin the app was given that the PC does not present means something is wrong on
    // the PC, or this is not the PC that was paired.
    if (machine.pins.isNotEmpty &&
        handshake.spkiSha256 != null &&
        !machine.pins.contains(handshake.spkiSha256)) {
      state = state.copyWith(
        busy: false,
        handshake: handshake,
        error: 'The PC is presenting a different TLS key than the one that was paired.',
        errorDetail:
            'Paired pin: ${_short(machine.pins.first)}\n'
            'Presenting now: ${_short(handshake.spkiSha256!)}\n\n'
            'If the certificate was regenerated on the PC, pair again or rotate the keys '
            'from the PC. Requests are being blocked rather than sent to an unverified '
            'key.',
      );
      return;
    }

    state = state.copyWith(
      busy: false,
      handshake: handshake,
      connectedAt: DateTime.now(),
      clearError: true,
    );
    await _loadWhoAmI();
  }

  Future<void> _loadWhoAmI() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    try {
      final json = await api.getJson('/auth/whoami');
      state = state.copyWith(whoAmI: WhoAmI.fromJson(json));
    } on EsiApiException catch (e) {
      // A scope problem here is informative, not fatal: everything the app can still do
      // keeps working, and the Settings screen shows the scopes the PC actually granted.
      state = state.copyWith(
        error: e.code == EsiProtocol.errForbiddenScope
            ? 'This phone is missing permissions on the PC.'
            : null,
        errorDetail: e.displayMessage,
      );
    } on EsiNetworkException catch (e) {
      state = state.copyWith(error: 'Lost contact with the PC.', errorDetail: e.message);
    }
  }

  /// The user-facing pull-to-refresh / retry button.
  Future<void> refresh() => _refresh(ref.read(activeMachineProvider)!);

  /// Re-reads the live socket state into the status, used by the status spine.
  void reportSocket(EsiSocketState next) =>
      state = state.copyWith(socketState: next);

  static String _short(String pin) =>
      pin.length <= 20 ? pin : '${pin.substring(0, 10)}…${pin.substring(pin.length - 8)}';
}

final connectionProvider =
    NotifierProvider<ConnectionNotifier, ConnectionStatus>(ConnectionNotifier.new);

/// A generic "load something, show its error" helper, so each feature screen does not
/// reimplement the same four states.
class AsyncSlice<T> {
  const AsyncSlice({
    this.value,
    this.loading = false,
    this.error,
    this.errorDetail,
    this.unsupported = false,
  });

  final T? value;
  final bool loading;
  final String? error;
  final String? errorDetail;

  /// True when the agent answered `404 esi_not_found`, which PROTOCOL.md §10 defines as
  /// "this agent is too old for that feature".
  final bool unsupported;

  bool get hasValue => value != null;
  bool get isEmpty => value == null && !loading && error == null;

  const AsyncSlice.busy({this.value, this.unsupported = false})
      : loading = true,
        error = null,
        errorDetail = null;

  AsyncSlice<T> withValue(T next) =>
      AsyncSlice<T>(value: next, loading: false, unsupported: unsupported);

  AsyncSlice<T> withError(String message, {String? detail}) => AsyncSlice<T>(
        value: value,
        loading: false,
        error: message,
        errorDetail: detail,
      );
}

/// Runs [call] and folds any protocol failure into an [AsyncSlice] instead of throwing, so
/// no screen has to write its own try/catch.
Future<AsyncSlice<T>> runSlice<T>(Future<T> Function() call) async {
  try {
    return AsyncSlice<T>(value: await call());
  } on EsiApiException catch (e) {
    if (e.isUnsupported) {
      return AsyncSlice<T>(
        unsupported: true,
        error: 'This agent does not support that yet.',
        errorDetail: 'Update the ESI Remote PC agent on the PC to use this screen.',
      );
    }
    if (e.isForbidden) {
      return AsyncSlice<T>(
        error: e.displayMessage,
        errorDetail: 'This phone is not permitted to use that. Grant it the missing '
            'scope on the PC, or pair again.',
      );
    }
    return AsyncSlice<T>(error: e.displayMessage);
  } on EsiNetworkException catch (e) {
    return AsyncSlice<T>(error: e.message);
  } on EsiTlsException catch (e) {
    return AsyncSlice<T>(error: e.failure.reason, errorDetail: e.failure.presented);
  }
}
