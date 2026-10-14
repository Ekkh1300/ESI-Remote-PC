import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../net/esi_api.dart';
import '../net/esi_ws.dart';
import '../protocol/models.dart';
import 'connection.dart';
import 'metrics.dart';

/// Everything the feature screens need, gathered in one file because they are all the same
/// shape: a signed GET, a model, and an action that reports what the agent actually said
/// rather than assuming success.

// =====================================================================================
// Gaming mode (§6.9)
// =====================================================================================

class GamingNotifier extends Notifier<AsyncSlice<GamingState>> {
  StreamSubscription<GamingState>? _stream;

  @override
  AsyncSlice<GamingState> build() {
    ref.onDispose(() => _stream?.cancel());
    // Deferred (D45): see metrics.dart. Both of these read and write `state`, and `build()` must
    // return a value before any of it runs.
    scheduleMicrotask(() {
      unawaited(refresh());
      unawaited(_watch());
    });
    return const AsyncSlice<GamingState>.busy();
  }

  Future<void> _watch() async {
    final socket = ref.read(webSocketProvider);
    if (socket == null) return;
    // `gaming.watch` is the server-side request; the state then arrives on the `gaming`
    // channel, so the screen updates without polling.
    _stream = socket.frames.gamingStates.listen((next) {
      state = AsyncSlice<GamingState>(value: next);
    });
    socket.send(EsiWsFrame.gamingWatch('g${DateTime.now().microsecondsSinceEpoch}'));
  }

  Future<void> refresh() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    state = await runSlice(() async {
      final json = await api.getJson('/gaming/state');
      return GamingState.fromJson(json);
    });
  }

  Future<({bool ok, String message})> setMode({
    required bool enabled,
    String? profileId,
    List<String>? suspendApps,
    String? launchAppId,
  }) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      final json = await api.postJson('/gaming/mode', body: <String, dynamic>{
        'enabled': enabled,
        'profileId': ?profileId,
        'suspendApps': ?suspendApps,
        'launchAppId': ?launchAppId,
      });
      final result = GamingModeResult.fromJson(json);
      await refresh();
      return (
        ok: result.enabled == enabled,
        message: result.warnings.isEmpty
            ? (enabled ? 'Gaming mode is on.' : 'Gaming mode is off.')
            : result.warnings.join(' · '),
      );
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }

  /// Suspends or resumes one background app. §6.9: `{"enabled": false}` suspends.
  Future<({bool ok, String message})> setAppSuspended({
    required String appId,
    required bool suspended,
  }) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      await api.postJson('/gaming/apps/${Uri.encodeComponent(appId)}/state', body: <String, dynamic>{
        'enabled': !suspended,
      });
      await refresh();
      return (ok: true, message: suspended ? '$appId suspended.' : '$appId resumed.');
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }
}

final gamingProvider =
    NotifierProvider<GamingNotifier, AsyncSlice<GamingState>>(GamingNotifier.new);

final gamingProfilesProvider = FutureProvider<List<GamingProfile>>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return const <GamingProfile>[];
  final slice = await runSlice(() async {
    final json = await api.getJson('/gaming/profiles');
    return (json['profiles'] is List)
        ? (json['profiles'] as List).map((e) => GamingProfile.fromJson(_obj(e))).toList()
        : <GamingProfile>[];
  });
  return slice.value ?? const <GamingProfile>[];
});

/// The best-effort presented-frame counter, which §6.9 says is exactly that.
final gamingFpsProvider = FutureProvider<int?>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return null;
  final slice = await runSlice(() async {
    final json = await api.getJson('/gaming/fps');
    return json['fps'] is int ? json['fps']! as int : null;
  });
  return slice.value;
});

Map<String, dynamic> _obj(Object? value) =>
    value is Map<String, dynamic> ? value : <String, dynamic>{};

// =====================================================================================
// App launcher (§6.5)
// =====================================================================================

final appCatalogProvider = FutureProvider<AppCatalog>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return const AppCatalog(apps: [], categories: []);
  final slice = await runSlice(() async {
    final json = await api.getJson('/apps');
    return AppCatalog.fromJson(json);
  });
  return valueOrThrow(slice, const AppCatalog(apps: [], categories: []));
});

final runningAppsProvider = FutureProvider<List<RunningApp>>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return const <RunningApp>[];
  final slice = await runSlice(() async {
    final json = await api.getJson('/apps/running');
    return (json['processes'] is List)
        ? (json['processes'] as List).map((e) => RunningApp.fromJson(_obj(e))).toList()
        : <RunningApp>[];
  });
  return valueOrThrow(slice, const <RunningApp>[]);
});

class AppActions {
  const AppActions(this._ref);
  final Ref _ref;

  Future<({bool ok, String message})> launch(AppEntry app) async {
    final api = _ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      final json = await api.postJson('/apps/launch', body: <String, dynamic>{'id': app.id});
      final result = LaunchResult.fromJson(json);
      _ref.invalidate(runningAppsProvider);
      return (
        ok: result.launched,
        message: result.launched
            ? '${app.name} started'
                '${result.processId != null ? ' (pid ${result.processId})' : ''}.'
            : (result.detail ?? '${app.name} did not start.'),
      );
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }

  /// `POST` asks the process to close, `DELETE` forces it (§6.5).
  Future<({bool ok, String message})> terminate(AppEntry app, {bool force = false}) async {
    final api = _ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      final path = '/apps/${Uri.encodeComponent(app.id)}/terminate';
      final json = force ? await api.deleteJson(path) : await api.postJson(path);
      final result = TerminateResult.fromJson(json);
      _ref.invalidate(runningAppsProvider);
      return (
        ok: result.terminated > 0,
        message: result.terminated > 0
            ? '${app.name} closed${result.forced ? ' forcefully' : ''}.'
            : '${app.name} is not running.',
      );
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }

  Future<({bool ok, String message})> killProcess(int pid) async {
    final api = _ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      await api.postJson('/system/process/$pid/terminate');
      _ref.invalidate(processesProvider);
      return (ok: true, message: 'Process $pid was asked to close.');
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }
}

final appActionsProvider = Provider<AppActions>(AppActions.new);

// =====================================================================================
// Media (§6.7)
// =====================================================================================

class MediaNotifier extends Notifier<AsyncSlice<MediaState>> {
  /// The volume request currently on the wire, if any.
  int? _inFlight;

  /// The newest volume asked for but not yet sent. At most one is ever held.
  int? _pending;

  /// Completes when the send loop has nothing left to do.
  Completer<void>? _idle;

  @override
  AsyncSlice<MediaState> build() {
    scheduleMicrotask(() => unawaited(refresh())); // Deferred (D45)
    return const AsyncSlice<MediaState>.busy();
  }

  Future<void> refresh() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    state = await runSlice(() async {
      final json = await api.getJson('/media/state');
      return MediaState.fromJson(json);
    });
  }

  /// Sets the volume, collapsing a whole drag into as few requests as possible.
  ///
  /// [wait] makes the caller wait for the value to actually reach the PC, which is what the
  /// release of a drag wants so it can report the outcome. A drag's intermediate previews pass
  /// `wait: false` and cost nothing to call.
  ///
  /// The collapsing is the whole point. Every call overwrites [_pending] and returns; only the
  /// send loop acts on it, and it sends one request at a time. Dragging a slider across its
  /// track therefore produces a handful of requests instead of a hundred, the last one wins,
  /// and the ones that never went out cannot come back later and undo the drag.
    Future<({bool ok, String message})> setVolume(int target, {bool wait = false}) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');

    _pending = target;
    final loop = _pump();

    if (!wait) return (ok: true, message: 'sent');

    await loop;
    final slice = state;
    return (
      ok: slice.error == null,
      message: slice.error ?? 'Volume set to $target.',
    );
  }

  /// Drains [_pending] one value at a time.
  ///
  /// The loop stops on [_pending] being empty *or* on the notifier being disposed - the second
  /// test is `ref.mounted`, positively. Written as `!ref.mounted` the loop exits immediately
  /// every time, the send never happens, and every test in this file passes except the ones
  /// that count requests. Nothing about the shape of the code gives that away.
  ///
  /// Never re-entered: a second caller finds [_inFlight] set and simply returns, leaving the
  /// newest value in [_pending] for the running loop to pick up when it finishes.
  Future<void> _pump() {
    final running = _idle;
    if (_inFlight != null && running != null) return running.future;

    final completer = Completer<void>();
    _idle = completer;

    () async {
      try {
        while (_pending != null && ref.mounted) {
          final value = _pending!;
          _pending = null;
          _inFlight = value;
          try {
            await _send('volumeSet', volume: value);
          } finally {
            _inFlight = null;
          }
        }
      } finally {
        if (!completer.isCompleted) completer.complete();
      }
    }();

    return completer.future;
  }

  Future<void> _send(String action, {int? step, int? volume}) async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    try {
      await api.postJson('/media/$action', body: <String, dynamic>{
        'step': ?step,
        'volume': ?volume,
      });
      await refresh();
    } on EsiApiException catch (e) {
      state = state.withError(e.displayMessage);
    } on EsiNetworkException catch (e) {
      state = state.withError(e.message);
    }
  }

  Future<({bool ok, String message})> act(String action, {int? step, int? volume}) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      final json = await api.postJson('/media/$action', body: <String, dynamic>{
        'step': ?step,
        'volume': ?volume,
      });
      final result = MediaActionResult.fromJson(json);
      await refresh();
      return (
        ok: result.accepted,
        message: result.accepted
            ? (result.detail ?? _humaniseAction(action))
            : (result.detail ?? 'The PC did not accept that.'),
      );
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }
}

String _humaniseAction(String action) {
  final spaced = action.replaceAllMapped(
    RegExp('([a-z])([A-Z])'),
    (m) => '${m[1]!} ${m[2]!.toLowerCase()}',
  );
  return '$spaced sent to the PC.';
}

final mediaProvider =
    NotifierProvider<MediaNotifier, AsyncSlice<MediaState>>(MediaNotifier.new);

// =====================================================================================
// Notifications (§6.10)
// =====================================================================================

class NotificationNotifier extends Notifier<AsyncSlice<NotificationPage>> {
  StreamSubscription<EsiNotification>? _stream;

  @override
  AsyncSlice<NotificationPage> build() {
    ref.onDispose(() => _stream?.cancel());
    // Deferred (D45): see metrics.dart.
    scheduleMicrotask(() {
      unawaited(refresh());
      unawaited(_watch());
    });
    return const AsyncSlice<NotificationPage>.busy();
  }

  Future<void> _watch() async {
    final socket = ref.read(webSocketProvider);
    if (socket == null) return;
    // New notifications arrive on the socket; the list is refetched rather than patched so
    // unread counts and retention stay the agent's answer rather than a local guess.
    _stream = socket.frames.notifications.listen((_) => refresh());
  }

  Future<void> refresh() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    state = await runSlice(() async {
      final json = await api.getJson(
        '/notifications',
        query: <(String, String)>[('limit', '100')],
      );
      return NotificationPage.fromJson(json);
    });
  }

  Future<void> markRead(int id) async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    await runSlice(() async => api.postJson('/notifications/$id/read'));
    await refresh();
  }

  Future<void> markAllRead() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    await runSlice(() async => api.postJson('/notifications/read-all'));
    await refresh();
  }

  Future<void> dismiss(int id) async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    await runSlice(() async => api.deleteJson('/notifications/$id'));
    await refresh();
  }

  Future<void> clearAll() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    await runSlice(() async => api.deleteJson('/notifications'));
    await refresh();
  }
}

final notificationsProvider =
    NotifierProvider<NotificationNotifier, AsyncSlice<NotificationPage>>(
  NotificationNotifier.new,
);

class NotificationRulesNotifier extends Notifier<AsyncSlice<List<NotificationRule>>> {
  @override
  AsyncSlice<List<NotificationRule>> build() {
    scheduleMicrotask(() => unawaited(refresh())); // Deferred (D45)
    return const AsyncSlice<List<NotificationRule>>.busy();
  }

  Future<void> refresh() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    state = await runSlice(() async {
      final json = await api.getJson('/notifications/rules');
      return (json['rules'] is List)
          ? (json['rules'] as List).map((e) => NotificationRule.fromJson(_obj(e))).toList()
          : <NotificationRule>[];
    });
  }

  /// Writes the whole rule set back. §6.10 has no per-rule endpoint, so the UI edits a
  /// local copy and `PUT`s the document.
  Future<({bool ok, String message})> save(List<NotificationRule> rules) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      await api.putJson('/notifications/rules', body: <String, dynamic>{
        'rules': <Map<String, dynamic>>[for (final r in rules) r.toJson()],
      });
      await refresh();
      return (ok: true, message: 'Notification rules saved on the PC.');
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }
}

final notificationRulesProvider =
    NotifierProvider<NotificationRulesNotifier, AsyncSlice<List<NotificationRule>>>(
  NotificationRulesNotifier.new,
);

// =====================================================================================
// Settings, devices, audit (§6.1, §6.11)
// =====================================================================================

class SettingsNotifier extends Notifier<AsyncSlice<AgentSettings>> {
  @override
  AsyncSlice<AgentSettings> build() {
    scheduleMicrotask(() => unawaited(refresh())); // Deferred (D45)
    return const AsyncSlice<AgentSettings>.busy();
  }

  Future<void> refresh() async {
    final api = ref.read(apiProvider);
    if (api == null) return;
    state = await runSlice(() async {
      final json = await api.getJson('/settings');
      return AgentSettings.fromJson(json);
    });
  }

  /// Round-trips the full document. The agent treats `PUT /settings` as a replace, so
  /// fields the app does not model are sent back exactly as they arrived.
  Future<({bool ok, String message})> save(AgentSettings next) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      final json = await api.putJson('/settings', body: next.toJson());
      final result = SettingsUpdateResult.fromJson(json);
      await refresh();
      return (
        ok: result.ok,
        message: result.warnings.isEmpty
            ? (result.restartRequired
                ? 'Saved. The agent has to restart before the change takes effect.'
                : 'Saved on the PC.')
            : result.warnings.join(' · '),
      );
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }

  Future<({bool ok, String message})> reset() async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      final json = await api.postJson('/settings/reset');
      final result = SettingsUpdateResult.fromJson(json);
      await refresh();
      return (
        ok: result.ok,
        message: result.restartRequired
            ? 'Settings reset. The agent has to restart before it takes effect.'
            : 'Settings reset on the PC.',
      );
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }
}

final settingsProvider =
    NotifierProvider<SettingsNotifier, AsyncSlice<AgentSettings>>(SettingsNotifier.new);

final pairedDevicesProvider = FutureProvider<List<PairedDevice>>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return const <PairedDevice>[];
  final slice = await runSlice(() async {
    final json = await api.getJson('/devices');
    return (json['devices'] is List)
        ? (json['devices'] as List).map((e) => PairedDevice.fromJson(_obj(e))).toList()
        : <PairedDevice>[];
  });
  return valueOrThrow(slice, const <PairedDevice>[]);
});

final auditTrailProvider = FutureProvider<AuditTrail>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return const AuditTrail(entries: <AuditEntry>[], chainValid: true);
  final slice = await runSlice(() async {
    final json = await api.getJson('/audit', query: <(String, String)>[('limit', '100')]);
    return AuditTrail.fromJson(json);
  });
  return slice.value ?? const AuditTrail(entries: <AuditEntry>[], chainValid: true);
});

final agentLogsProvider = FutureProvider<LogTail>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return const LogTail(lines: <LogLine>[], truncated: false, logDirectory: null);
  final slice = await runSlice(() async {
    final json = await api.getJson('/logs', query: <(String, String)>[('tail', '200')]);
    return LogTail.fromJson(json);
  });
  return slice.value ??
      const LogTail(lines: <LogLine>[], truncated: false, logDirectory: null);
});

// =====================================================================================
// Remote control (§6.8, §7.3)
// =====================================================================================

const RemoteSessionStatus unknownRemoteStatus = RemoteSessionStatus(
  available: false,
  sessionAgentState: 'unknown',
  active: false,
  streaming: false,
  connectedDeviceId: null,
  captureSource: 'gdi',
  maxFps: 30,
  inputEnabled: false,
  displays: <DisplayInfo>[],
  capabilities: <String>[],
);

final remoteStatusProvider = FutureProvider<RemoteSessionStatus>((ref) async {
  final api = ref.read(apiProvider);
  if (api == null) return unknownRemoteStatus;
  final slice = await runSlice(() async {
    final json = await api.getJson('/remote/session');
    return RemoteSessionStatus.fromJson(json);
  });
  return slice.value ?? unknownRemoteStatus;
});

final remoteHandleProvider =
    Provider<EsiWebSocketHandle?>((ref) => ref.watch(webSocketProvider));

class RemoteInputActions {
  const RemoteInputActions(this._ref);
  final Ref _ref;

  EsiWebSocketHandle? get _socket => _ref.read(remoteHandleProvider);

  void start({
    required int displayId,
    required int fps,
    required int quality,
    List<int> grid = const <int>[4, 4],
  }) =>
      _socket?.startRemote(displayId: displayId, fps: fps, quality: quality, grid: grid);

  void stop() => _socket?.stopRemote();

  void move(double x, double y) => _socket?.sendInput(<String, dynamic>{
        'kind': 'mouseMove',
        'x': x,
        'y': y,
      });

  void button(String button, bool down, double x, double y) =>
      _socket?.sendInput(<String, dynamic>{
        'kind': 'mouseButton',
        'button': button,
        'down': down,
        'x': x,
        'y': y,
      });

  void wheel(double dx, double dy) => _socket?.sendInput(<String, dynamic>{
        'kind': 'mouseWheel',
        'deltaX': dx.round(),
        'deltaY': dy.round(),
      });

  void key({
    required String code,
    required bool down,
    String? text,
    List<String> mods = const <String>[],
  }) =>
      _socket?.sendKey(code: code, down: down, text: text, mods: mods);

  void text(String text) =>
      _socket?.sendInput(<String, dynamic>{'kind': 'text', 'text': text});
}

final remoteInputActionsProvider = Provider<RemoteInputActions>(RemoteInputActions.new);
