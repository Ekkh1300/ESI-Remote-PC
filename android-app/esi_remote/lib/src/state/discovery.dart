import 'dart:async';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../net/esi_api.dart';
import '../net/discovery.dart';
import '../net/wol.dart';
import '../protocol/models.dart';
import 'connection.dart';

/// How a discovery pass is performed, as a function so tests can replace the network work
/// without a seam in the UI.
typedef DiscoveryRunner = Future<DiscoveryResult> Function({
  void Function(DiscoveryMethod)? onProgress,
});

final discoveryRunnerProvider = Provider<DiscoveryRunner>(
  (ref) => ({void Function(DiscoveryMethod)? onProgress}) =>
      LanDiscovery().scan(onProgress: onProgress),
);

/// State of the onboarding screen's search.
class DiscoveryNotifier extends Notifier<DiscoveryState> {
  @override
  DiscoveryState build() => const DiscoveryState();

  Future<void> scan() async {
    state = state.copyWith(scanning: true, clearError: true);
    final result = await ref.read(discoveryRunnerProvider)(
      onProgress: (method) => state = state.copyWith(
        channelsSeen: <DiscoveryMethod>{...state.channelsSeen, method},
      ),
    );
    if (!ref.mounted) return;
    state = DiscoveryState(
      scanning: false,
      agents: result.agents,
      notes: result.notes,
      channelsSeen: result.methods,
    );
  }

  /// Confirms a hand-typed address and, where the agent answers, shows what it says about
  /// itself so the user can check it against the PC before pairing.
  Future<Handshake?> probe(String host, int port) async {
    final endpoint = EsiEndpoint(host: host, port: port, pins: const <String>[]);
    state = state.copyWith(probing: true, clearError: true);
    final handshake = await readHandshakeUnpinned(endpoint);
    state = state.copyWith(
      probing: false,
      probe: handshake,
      error: handshake == null
          ? 'Nothing answered at $host:$port. Check the address, the port, and that the '
              'phone is on the same network as the PC.'
          : null,
    );
    return handshake;
  }

  void dismissProbe() => state = state.copyWith(clearProbe: true);
}

class DiscoveryState {
  const DiscoveryState({
    this.scanning = false,
    this.probing = false,
    this.agents = const <DiscoveredAgent>[],
    this.notes = const <String>[],
    this.channelsSeen = const <DiscoveryMethod>{},
    this.probe,
    this.error,
  });

  final bool scanning;
  final bool probing;
  final List<DiscoveredAgent> agents;

  /// Everything the channels said, including why a channel found nothing. Rendered
  /// verbatim: a failed search with no explanation is the least useful state a scanner can
  /// be in.
  final List<String> notes;

  final Set<DiscoveryMethod> channelsSeen;

  /// The handshake from a hand-typed address, if one answered.
  final Handshake? probe;
  final String? error;

  DiscoveryState copyWith({
    bool? scanning,
    bool? probing,
    List<DiscoveredAgent>? agents,
    List<String>? notes,
    Set<DiscoveryMethod>? channelsSeen,
    Handshake? probe,
    String? error,
    bool clearError = false,
    bool clearProbe = false,
  }) =>
      DiscoveryState(
        scanning: scanning ?? this.scanning,
        probing: probing ?? this.probing,
        agents: agents ?? this.agents,
        notes: notes ?? this.notes,
        channelsSeen: channelsSeen ?? this.channelsSeen,
        probe: clearProbe ? null : (probe ?? this.probe),
        error: clearError ? null : (error ?? this.error),
      );
}

final discoveryProvider =
    NotifierProvider<DiscoveryNotifier, DiscoveryState>(DiscoveryNotifier.new);

/// Sends the Wake-on-LAN magic packet and reports what happened, including the
/// preconditions the agent flagged, because "sent 6 packets" is not the same as "it woke
/// up" and only the PC can say which one happened.
class WakeNotifier extends Notifier<AsyncSlice<WakeOutcome>> {
  @override
  AsyncSlice<WakeOutcome> build() => const AsyncSlice<WakeOutcome>();

  Future<void> wake(String macAddress) async {
    state = const AsyncSlice<WakeOutcome>.busy();
    final api = ref.read(apiProvider);
    final readiness = api == null
        ? null
        : await runSlice(() async {
            final json = await api.getJson('/power/wol/readiness');
            return WolReadiness.fromJson(json);
          }).then((s) => s.value);

    try {
      final sent = await const WakeOnLan().send(
        macAddress,
        broadcastAddresses: readiness?.broadcastAddresses,
      );
      state = AsyncSlice<WakeOutcome>(value: WakeOutcome(sent: sent, readiness: readiness));
    } on FormatException catch (e) {
      state = AsyncSlice<WakeOutcome>(error: e.message);
    } on SocketException catch (e) {
      state = AsyncSlice<WakeOutcome>(error: e.osError?.message ?? e.message);
    } on EsiNetworkException catch (e) {
      state = AsyncSlice<WakeOutcome>(error: e.message);
    }
  }
  /// Relays the magic packet through the agent instead of sending it from the phone.
  ///
  /// The phone sending it directly is the normal path (§6.4); this exists so the user can
  /// tell the two apart when the phone is on a different subnet from the PC.
  Future<({bool ok, String message})> relay(String macAddress) async {
    final api = ref.read(apiProvider);
    if (api == null) return (ok: false, message: 'No PC is selected.');
    try {
      final json = await api.postJson('/power/wake', body: <String, dynamic>{
        'macAddress': macAddress,
      });
      final sent = json['sent'] is int ? json['sent']! as int : 0;
      return (ok: true, message: 'The agent sent $sent magic packets.');
    } on EsiApiException catch (e) {
      return (ok: false, message: e.displayMessage);
    } on EsiNetworkException catch (e) {
      return (ok: false, message: e.message);
    }
  }
}

class WakeOutcome {
  const WakeOutcome({required this.sent, this.readiness});
  final int sent;
  final WolReadiness? readiness;
}

final wakeProvider =
    NotifierProvider<WakeNotifier, AsyncSlice<WakeOutcome>>(WakeNotifier.new);
