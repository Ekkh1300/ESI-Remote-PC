import 'dart:async';
import 'dart:collection';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../net/esi_api.dart';
import '../net/esi_ws.dart';
import '../protocol/models.dart';
import 'connection.dart';

/// The dashboard's live metrics.
///
/// Fed by the WebSocket stream when it is up, and by an explicit `GET /system/metrics`
/// when it is not, because a dashboard that only works over the socket is a dashboard that
/// shows nothing on a flaky Wi-Fi link. The two paths converge on the same state, and the
/// UI shows which one is live.
class MetricsNotifier extends Notifier<MetricsState> {
  StreamSubscription<MetricsSnapshot>? _stream;
  StreamSubscription<EsiWsError>? _errors;
  Timer? _fallbackTimer;

  @override
  MetricsState build() {
    ref.onDispose(() {
      _stream?.cancel();
      _errors?.cancel();
      _fallbackTimer?.cancel();
    });
    // Deferred on purpose (D45). `_start()` reads and writes `state`, and calling it straight
    // from `build()` starts that work before `build()` has returned its first value - so
    // `refresh()` reached `state` while the provider was still uninitialised and threw
    // "Tried to read the state of an uninitialized provider".
    //
    // Nothing caught it: an unhandled async error does not stop the app, it just tears down
    // this provider. Which is why the Screen tab then claimed the "remote" capability was
    // switched off - `connection.handshake` was never populated, `supports('remote')` fell
    // through its `?? false`, and a perfectly working feature reported itself as disabled.
    // One uninitialised provider took the whole screen with it.
    //
    // Scheduling after the microtask queue drains means `build()` has returned and the provider
    // owns a state before anything touches it.
    scheduleMicrotask(_start);
    return const MetricsState();
  }

  Future<void> _start() async {
    final socket = ref.read(webSocketProvider);
    final api = ref.read(apiProvider);
    if (socket == null || api == null) {
      await refresh();
      return;
    }

    // The socket may already be live, in which case `_start` has nothing to subscribe to
    // and the fallback poll covers the gap.
    _stream = socket.frames.metricsSnapshots.listen(_onSnapshot);
    _errors = socket.frames.errors.listen(_onWsError);

    socket.stateChanges.listen((next) {
      if (next == EsiSocketState.live) {
        _stopFallback();
      } else {
        _startFallback();
      }
    });

    if (socket.isLive) {
      state = state.copyWith(source: MetricsSource.stream);
    } else {
      await refresh();
      _startFallback();
    }
  }

  void _onSnapshot(MetricsSnapshot snapshot) {
    state = state
        .withSnapshot(snapshot)
        .copyWith(source: MetricsSource.stream, stale: false, error: null);
  }

  void _onWsError(EsiWsError error) {
    // A 409 on `remote.start` is not a metrics problem; the remote screen handles its own
    // errors. Only surface what the user would otherwise see as a frozen dashboard.
    if (error.code == 'esi_conflict' || error.code == 'esi_forbidden_scope') return;
    state = state.copyWith(error: error.message);
  }

  /// One-shot fetch, used by pull-to-refresh and whenever the stream is not running.
  Future<void> refresh() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    state = state.copyWith(loading: true, error: null);
    final slice = await runSlice(
      () async => MetricsSnapshot.fromJson(
        await api.getJson('/system/metrics', query: <(String, String)>[('window', '1000')]),
      ),
    );
    if (slice.error != null) {
      state = state.copyWith(
        loading: false,
        error: slice.error,
        errorDetail: slice.errorDetail,
      );
      return;
    }
    state = state
        .withSnapshot(slice.value!)
        .copyWith(
          loading: false,
          stale: false,
          error: null,
          source: state.source == MetricsSource.stream ? MetricsSource.stream : MetricsSource.poll,
        );
  }

  /// A slow poll for when the socket is down, so the dashboard still moves.
  void _startFallback() {
    if (state.source == MetricsSource.stream) {
      state = state.copyWith(source: MetricsSource.poll);
    }
    _fallbackTimer?.cancel();
    _fallbackTimer = Timer.periodic(const Duration(seconds: 3), (_) => refresh());
  }

  void _stopFallback() {
    _fallbackTimer?.cancel();
    _fallbackTimer = null;
    state = state.copyWith(source: MetricsSource.stream);
  }
}

enum MetricsSource { none, stream, poll }

/// Metrics plus how they got here. [stale] is what the UI shows on the timestamp, so a
/// number that has not moved for a minute never looks live.
class MetricsState {
  const MetricsState({
    this.snapshot,
    this.source = MetricsSource.none,
    this.loading = false,
    this.stale = false,
    this.error,
    this.errorDetail,
    this.cpuHistory = const <double>[],
    this.gpuHistory = const <double>[],
    this.ramHistory = const <double>[],
  });

  final MetricsSnapshot? snapshot;
  final MetricsSource source;
  final bool loading;
  final bool stale;
  final String? error;
  final String? errorDetail;

  /// Rolling windows for the load trace. The agent's own `load` array is preferred when it
  /// has data, so the trace shows the machine's real past rather than only the time the
  /// app has been open.
  final List<double> cpuHistory;
  final List<double> gpuHistory;
  final List<double> ramHistory;

  bool get hasData => snapshot != null;

  static const int _historyLength = 120;

  MetricsState withSnapshot(MetricsSnapshot next) {
    final cpu = <double>[...cpuHistory, next.cpu.usagePercent];
    final gpu = <double>[...gpuHistory, next.gpuAggregate.usagePercent];
    final ram = <double>[...ramHistory, next.memory.usedPercent];
    final fromAgent = next.load.length >= 2;
    return MetricsState(
      snapshot: next,
      source: source,
      cpuHistory: fromAgent ? next.load.map((p) => p.cpu).toList() : _trim(cpu),
      gpuHistory: fromAgent ? next.load.map((p) => p.gpu).toList() : _trim(gpu),
      ramHistory: fromAgent ? next.load.map((p) => p.ram).toList() : _trim(ram),
    );
  }

  static List<double> _trim(List<double> values) => values.length <= _historyLength
      ? values
      : values.sublist(values.length - _historyLength);

  MetricsState copyWith({
    MetricsSource? source,
    bool? loading,
    bool? stale,
    String? error,
    String? errorDetail,
    bool clearError = false,
  }) =>
      MetricsState(
        snapshot: snapshot,
        source: source ?? this.source,
        loading: loading ?? this.loading,
        stale: stale ?? this.stale,
        error: clearError ? null : (error ?? this.error),
        errorDetail: clearError ? null : (errorDetail ?? this.errorDetail),
        cpuHistory: cpuHistory,
        gpuHistory: gpuHistory,
        ramHistory: ramHistory,
      );
}

final metricsProvider =
    NotifierProvider<MetricsNotifier, MetricsState>(MetricsNotifier.new);

/// `GET /system/info`, the once-per-screen-load hardware inventory. Kept out of the
/// metrics slice because it does not change at 1 Hz and is much larger.
final systemInfoProvider = FutureProvider<SystemInfo?>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return null;
  return runSlice(() async {
    final json = await api.getJson('/system/info', query: <(String, String)>[('detail', 'full')]);
    return SystemInfo.fromJson(json);
  }).then((slice) => slice.value);
});

/// Power status. Refreshed on demand and after every action, because a sleep or a
/// shutdown is not observable through the metrics stream.
class PowerNotifier extends Notifier<AsyncSlice<PowerStatus>> {
  @override
  AsyncSlice<PowerStatus> build() {
    // Deferred (D45): see MetricsNotifier above. `refresh()` assigns to `state`, and build()
    // has to return before there is a state to assign to.
    scheduleMicrotask(() => unawaited(refresh()));
    return const AsyncSlice<PowerStatus>.busy();
  }

  Future<void> refresh() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    state = await runSlice(() async {
      final json = await api.getJson('/power/status');
      return PowerStatus.fromJson(json);
    });
  }

  /// Issues a power action and refreshes the status. Returns the agent's answer so the
  /// caller can show a warning or a refusal rather than assuming success.
  Future<({bool ok, String message})> act(
    PowerActionRequest request,
  ) async {
    final api = ref.read(apiProvider);
    if (api == null) {
      return (ok: false, message: 'No PC is selected.');
    }
    try {
      final json = await api.postJson(
        '/power/${request.action.wire}',
        body: <String, dynamic>{
          'force': request.force,
          'delaySeconds': request.delaySeconds,
          if (request.dialogMessage != null) 'message': request.dialogMessage,
        },
      );
      final result = PowerActionResult.fromJson(json);
      await refresh();
      final parts = <String>[
        if (result.effectiveMode != null) 'mode: ${result.effectiveMode}',
        if (result.warning != null) result.warning!,
        if (result.detail != null) result.detail!,
      ];
      return (ok: result.accepted, message: parts.isEmpty ? 'Accepted.' : parts.join(' · '));
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException {
      // A shutdown that takes the machine down mid-request looks exactly like this, so it
      // is worth saying so rather than reporting a failure.
      return (
        ok: true,
        message: 'No reply from the PC. If the request went through, the machine is now '
            'going down, which is expected.',
      );
    }
  }

  Future<WolReadiness?> readiness() async {
    final api = ref.read(apiProvider);
    if (api == null) return null;
    final slice = await runSlice(() async {
      final json = await api.getJson('/power/wol/readiness');
      return WolReadiness.fromJson(json);
    });
    return slice.value;
  }
}

final powerProvider =
    NotifierProvider<PowerNotifier, AsyncSlice<PowerStatus>>(PowerNotifier.new);

/// A power action as the UI describes it: what it will do, and how risky it is.
class PowerActionRequest {
  const PowerActionRequest({
    required this.action,
    required this.title,
    required this.question,
    required this.consequence,
    this.destructive = true,
    this.requiresTyping,
    this.dialogMessage,
    this.force = false,
    this.delaySeconds = 5,
  });

  final EsiPowerAction action;
  final String title;

  /// The question asked before anything happens.
  final String question;

  /// What the PC will actually do, in the words a person at the keyboard would use.
  final String consequence;

  final bool destructive;

  /// The confirmation text shown in the shutdown dialog on the PC itself.
  final String? dialogMessage;

  final String? requiresTyping;
  final bool force;
  final int delaySeconds;

  @override
  String toString() => 'PowerActionRequest($action, "$title")';
}

/// The app's own enum of power actions, so screens do not pass raw strings around.
enum EsiPowerAction { shutdown, restart, sleep, hibernate, lock, signout, forceRestart, wake }

extension EsiPowerActionWire on EsiPowerAction {
  /// The path segment the agent expects, or null for Wake-on-LAN which the phone sends
  /// itself (PROTOCOL.md §6.4).
  String? get wire => switch (this) {
        EsiPowerAction.shutdown => 'shutdown',
        EsiPowerAction.restart => 'restart',
        EsiPowerAction.sleep => 'sleep',
        EsiPowerAction.hibernate => 'hibernate',
        EsiPowerAction.lock => 'lock',
        EsiPowerAction.signout => 'signout',
        EsiPowerAction.forceRestart => 'forceRestart',
        EsiPowerAction.wake => null,
      };

  String get label => switch (this) {
        EsiPowerAction.shutdown => 'Shut down',
        EsiPowerAction.restart => 'Restart',
        EsiPowerAction.sleep => 'Sleep',
        EsiPowerAction.hibernate => 'Hibernate',
        EsiPowerAction.lock => 'Lock',
        EsiPowerAction.signout => 'Sign out',
        EsiPowerAction.forceRestart => 'Force restart',
        EsiPowerAction.wake => 'Wake',
      };

  /// `lock` and `signout` only work when someone is signed in; the agent routes them
  /// through the interactive session and refuses otherwise.
  bool get needsSession => this == EsiPowerAction.lock || this == EsiPowerAction.signout;
}

/// The catalogue of actions offered on the power screen, with the copy that goes with them.
const List<PowerActionRequest> powerActionCatalogue = <PowerActionRequest>[
  PowerActionRequest(
    action: EsiPowerAction.lock,
    title: 'Lock',
    question: 'Lock this PC now?',
    consequence: 'The screen locks immediately. Whoever is using it will need the password.',
    destructive: false,
    delaySeconds: 0,
  ),
  PowerActionRequest(
    action: EsiPowerAction.signout,
    title: 'Sign out',
    question: 'Sign the current user out?',
    consequence: 'Unsaved work in every open application is at risk. This is not a sleep.',
  ),
  PowerActionRequest(
    action: EsiPowerAction.sleep,
    title: 'Sleep',
    question: 'Put this PC to sleep?',
    consequence: 'The machine stays powered and wakes on a key press, a network packet or '
        'Wake-on-LAN. Anything not saved to disk is still only in memory.',
  ),
  PowerActionRequest(
    action: EsiPowerAction.hibernate,
    title: 'Hibernate',
    question: 'Hibernate this PC?',
    consequence: 'Memory is written to disk and the machine powers off. It takes longer '
        'than sleep and wakes more slowly, but nothing is lost on a flat battery.',
  ),
  PowerActionRequest(
    action: EsiPowerAction.restart,
    title: 'Restart',
    question: 'Restart this PC?',
    consequence: 'Windows will close applications that ask it to. Applications that block '
        'the shutdown will keep the machine alive until they are dealt with.',
    requiresTyping: 'RESTART',
  ),
  PowerActionRequest(
    action: EsiPowerAction.shutdown,
    title: 'Shut down',
    question: 'Shut this PC down?',
    consequence: 'The machine powers off. Wake-on-LAN has to be armed in the BIOS and in '
        'the network adapter for it to come back.',
    requiresTyping: 'SHUTDOWN',
  ),
  PowerActionRequest(
    action: EsiPowerAction.forceRestart,
    title: 'Force restart',
    question: 'Force restart without warning?',
    consequence: 'Windows restarts immediately. Open applications are killed without a '
        'chance to save, and a force restart can lose data that was only in memory.',
    requiresTyping: 'FORCE',
    force: true,
    delaySeconds: 0,
  ),
];

/// A read-only view of the process list, with a client-side sort so the user can reorder
/// without another round trip.
final processesProvider = FutureProvider<List<ProcessInfo>>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return const <ProcessInfo>[];
  final slice = await runSlice(() async {
    final json = await api.getJson('/system/processes', query: <(String, String)>[
      ('sort', 'cpu'),
      ('limit', '150'),
    ]);
    return (json['processes'] is List)
        ? (json['processes'] as List)
            .map((e) => ProcessInfo.fromJson(asObjectOf(e)))
            .toList()
        : <ProcessInfo>[];
  });
  return valueOrThrow(slice, const <ProcessInfo>[]);
});

/// A tiny local helper so the process list does not need to import the JSON readers.
Map<String, dynamic> asObjectOf(Object? value) =>
    value is Map<String, dynamic> ? value : <String, dynamic>{};

/// Keeps the unmodifiable view the dashboard wants.
List<ProcessInfo> topProcesses(List<ProcessInfo> all, int count) =>
    UnmodifiableListView(all.take(count));
