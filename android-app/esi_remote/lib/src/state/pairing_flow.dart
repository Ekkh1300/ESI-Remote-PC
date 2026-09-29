import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../data/machine_store.dart';
import '../data/pairing_service.dart';
import '../net/esi_api.dart';
import 'connection.dart';

/// How the endpoint being paired was learned.
enum PairingSource {
  /// Typed by hand.
  manual,

  /// Read from the pairing QR code on the PC.
  qr,

  /// Found by a search, then confirmed by hand.
  discovered,
}

/// Everything the pairing screen needs, handed over from onboarding.
class PairingRequest {
  const PairingRequest({
    required this.endpoint,
    required this.provisioningSecret,
    required this.source,
    this.macAddress,
    this.nickname,
  });

  final EsiEndpoint endpoint;

  /// The one-time secret from §5.4. Without it the agent approves but never releases the
  /// credentials, so this is required rather than optional.
  final Uint8List provisioningSecret;

  final PairingSource source;
  final String? macAddress;
  final String? nickname;
}

/// Owns one pairing attempt.
///
/// The whole handshake lives here rather than in the widget so that rotating the phone, or
/// navigating away and back, does not abandon a code the user is in the middle of
/// approving on the PC.
class PairingFlowNotifier extends Notifier<PairingFlowState> {
  final PairingService _service = PairingService();

  @override
  PairingFlowState build() => const PairingFlowState();

  Future<void> start(PairingRequest request) async {
    state = PairingFlowState(
      request: request,
      progress: const PairingProgress(phase: PairingPhase.requesting),
      startedAt: DateTime.now(),
    );

    final result = await _service.pair(
      endpoint: request.endpoint,
      deviceName: _deviceName,
      osVersion: _osVersion,
      appVersion: _appVersion,
      provisioningSecret: request.provisioningSecret,
      onProgress: (progress) {
        if (state.progress?.isTerminal ?? false) return;
        state = state.copyWith(progress: progress);
      },
      onTick: () => state = state.copyWith(tick: state.tick + 1),
    );

    state = state.copyWith(progress: result, finished: true);
  }

  /// Cancels the phone's side. The attempt on the PC stays pending and expires on its own,
  /// which the screen says out loud.
  void cancel() {
    state = state.copyWith(
      progress: PairingProgress(
        phase: PairingPhase.cancelled,
        pairingId: state.progress?.pairingId,
        userCode: state.progress?.userCode,
        error: 'Cancelled on the phone. The code on the PC expires on its own.',
      ),
      finished: true,
    );
  }

  void reset() => state = const PairingFlowState();

  static const String _appVersion = '1.0.0';
  static const String _osVersion = '16';
  static const String _deviceName = 'Android phone';
}

class PairingFlowState {
  const PairingFlowState({
    this.request,
    this.progress,
    this.startedAt,
    this.finished = false,
    this.tick = 0,
  });

  final PairingRequest? request;
  final PairingProgress? progress;
  final DateTime? startedAt;
  final bool finished;

  /// Increments once per poll, so a countdown can re-render without owning a timer.
  final int tick;

  PairingFlowState copyWith({
    PairingRequest? request,
    PairingProgress? progress,
    DateTime? startedAt,
    bool? finished,
    int? tick,
  }) =>
      PairingFlowState(
        request: request ?? this.request,
        progress: progress ?? this.progress,
        startedAt: startedAt ?? this.startedAt,
        finished: finished ?? this.finished,
        tick: tick ?? this.tick,
      );
}

final pairingFlowProvider =
    NotifierProvider<PairingFlowNotifier, PairingFlowState>(PairingFlowNotifier.new);

/// Stores a completed pairing and makes it the active machine.
Future<PairedMachine> completePairing({
  required WidgetRef ref,
  required PairingRequest request,
  required String deviceId,
  required Uint8List masterSecret,
  required int keyId,
  String? agentVersion,
  String? deviceName,
}) async {
  final machine = PairedMachine(
    deviceId: deviceId,
    host: request.endpoint.host,
    port: request.endpoint.port,
    pins: request.endpoint.pins,
    keyId: keyId,
    masterSecret: masterSecret,
    nickname: request.nickname ?? deviceName ?? request.endpoint.host,
    macAddress: request.macAddress,
    pairedAt: DateTime.now(),
    lastSeenAt: DateTime.now(),
    agentVersion: agentVersion ?? '',
    features: const <String, bool>{},
  );
  await ref.read(machineListProvider.notifier).add(machine);
  await ref.read(activeDeviceIdProvider.notifier).select(deviceId);
  return machine;
}
