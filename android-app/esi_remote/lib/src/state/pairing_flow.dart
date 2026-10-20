import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../app_info.dart';
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

  /// The one-time secret from §5.4.
  ///
  /// It is deliberately allowed to be empty when the attempt starts, because it cannot
  /// exist yet: the agent mints it while handling `POST /pair/request`, and the only
  /// things that ever carry it off the PC are the QR on the approval prompt and the QR on
  /// `/pairing?code=…` — both of which need the attempt to already be running. Requiring
  /// it up front made pairing impossible to start from the phone at all.
  ///
  /// Without it the agent still approves the attempt, but never releases the credentials,
  /// so it has to arrive by scanning that QR while this attempt is still live.
  final Uint8List provisioningSecret;

  /// True once a scanned QR has supplied the secret this attempt needs.
  bool get hasSecret => provisioningSecret.isNotEmpty;

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
      appVersion: esiAppVersion,
      // The secret is not passed in: the service creates it and sends it with the request
      // (D39), so there is nothing to scan part-way through and no way for it to be wrong.

      onProgress: (progress) {
        if (state.progress?.isTerminal ?? false) return;
        state = state.copyWith(progress: progress);
      },
      onTick: () => state = state.copyWith(tick: state.tick + 1),
    );

    state = state.copyWith(progress: result, finished: true);
  }

  /// Adopts the one-time secret from a QR scanned while this attempt is running.
  ///
  /// This is the other half of [PairingRequest.provisioningSecret]: the attempt has to be
  /// created before the PC will show a QR, and the QR is what carries the secret back. The
  /// attempt object in the QR's polling URL is the same one already running, so adopting
  /// the secret in place is what the protocol expects — it does not start a second attempt,
  /// and a second attempt would be a different secret the PC has never seen.
  void adoptSecret(Uint8List secret) {
    final request = state.request;
    if (request == null || secret.isEmpty) return;

    state = state.copyWith(
      request: PairingRequest(
        endpoint: request.endpoint,
        provisioningSecret: secret,
        source: request.source,
        macAddress: request.macAddress,
        nickname: request.nickname,
      ),
    );
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
