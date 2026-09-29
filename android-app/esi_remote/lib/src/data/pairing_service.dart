import 'dart:async';
import 'dart:typed_data';

import '../net/esi_api.dart';
import '../protocol/esi_crypto.dart';
import '../protocol/models.dart';

/// Where a pairing attempt has got to.
enum PairingPhase {
  /// Asking the agent to open a pairing attempt.
  requesting,

  /// The agent has an attempt open and is showing it to the human on the PC.
  awaitingApproval,

  /// Approved; unwrapping the secrets.
  decrypting,

  done,
  failed,
  cancelled,
}

/// A snapshot of the pairing attempt, for the UI to render.
class PairingProgress {
  const PairingProgress({
    required this.phase,
    this.pairingId,
    this.userCode,
    this.expiresAt,
    this.approvalMethod,
    this.deviceName,
    this.approval = PairingProgressApproval.none,
    this.message,
    this.error,
  });

  final PairingPhase phase;
  final String? pairingId;

  /// The six-digit code shown on the PC, so the human can match the two screens.
  final String? userCode;
  final int? expiresAt;

  /// `desktop-prompt` or `browser-or-cli`. The latter means nobody is signed in on the PC, so
  /// there is no desktop to prompt on, and the approval has to happen on the pairing page in
  /// a browser or from the console.
  final String? approvalMethod;
  final String? deviceName;
  final PairingProgressApproval approval;
  final String? message;
  final String? error;

  bool get isTerminal =>
      phase == PairingPhase.done ||
      phase == PairingPhase.failed ||
      phase == PairingPhase.cancelled;

  int? get secondsRemaining {
    final at = expiresAt;
    if (at == null) return null;
    final remaining = at - DateTime.now().millisecondsSinceEpoch ~/ 1000;
    return remaining < 0 ? 0 : remaining;
  }
}

/// Drives the little "waiting for approval" indicator on the pairing screen. Separate
/// from [PairingPhase] because the two advance independently: the approval animation
/// ticks while the phase is still [PairingPhase.awaitingApproval].
enum PairingProgressApproval { none, inProgress, complete }

/// Runs the pairing handshake of PROTOCOL.md §3.2.
///
/// Two properties matter more than anything else here:
///
/// * The secrets are released **exactly once**. If decryption fails, the agent has already
///   marked the attempt consumed and the user has to start again; that is reported
///   plainly rather than retried into a confusing state.
/// * The provisioning secret from the QR is the only thing that can open the envelope, so
///   a racing attacker who got the desktop prompt still cannot become a paired device.
class PairingService {
  PairingService();

  /// A client that talks to an unpaired agent: no signature headers, because there is no
  /// key yet, and the pin still enforced because the QR gave us one.
  EsiApi _unpairedClient(EsiEndpoint endpoint) =>
      EsiApi(endpoint: endpoint, identity: _unpairedIdentity);

  /// A placeholder identity. The unauthenticated endpoints reject requests that carry no
  /// signature headers anyway, and a real identity cannot exist before pairing.
  static final EsiIdentity _unpairedIdentity = EsiIdentity(
    deviceId: '0' * 16,
    keyId: 1,
    masterSecret: Uint8List(0),
  );

  /// `POST /api/v1/pair/request`, then poll `GET /api/v1/pair/session`.
  ///
  /// [onProgress] is called on every state change so the UI can narrate; the returned
  /// future completes when the attempt reaches a terminal state.
  Future<PairingProgress> pair({
    required EsiEndpoint endpoint,
    required String deviceName,
    required Uint8List provisioningSecret,
    String? osVersion,
    String? appVersion,
    required void Function(PairingProgress) onProgress,
    Future<bool> Function()? confirm,
    void Function()? onTick,
  }) async {
    final api = _unpairedClient(endpoint);
    PairRequestResult created;

    try {
      onProgress(const PairingProgress(phase: PairingPhase.requesting));
      created = await _request(api, deviceName, osVersion, appVersion);
    } on EsiApiException catch (e) {
      final progress = _failure(e.displayMessage);
      onProgress(progress);
      return progress;
    } on EsiNetworkException catch (e) {
      final progress = _failure(e.message);
      onProgress(progress);
      return progress;
    } on Object catch (e) {
      final progress = _failure('The pairing request could not be sent: $e');
      onProgress(progress);
      return progress;
    } finally {
      api.close();
    }

    var progress = PairingProgress(
      phase: PairingPhase.awaitingApproval,
      pairingId: created.pairingId,
      userCode: created.userCode,
      expiresAt: created.expiresAt,
      approvalMethod: created.approval,
      deviceName: deviceName,
      approval: PairingProgressApproval.inProgress,
      message: created.approval == 'desktop-prompt'
          ? 'Approve this device in the prompt on ${_hostOf(endpoint)}.'
          : 'Nobody is signed in on the PC, so there is no prompt to approve. Stop the '
              'agent, run: esirc pair approve ${created.userCode} , then start the agent '
              'again. It cannot be approved in a browser on purpose.',
    );
    onProgress(progress);

    // The agent asks for one poll per second and closes the attempt after
    // `pairingWindowSeconds` (120 s by default).
    final interval = Duration(
      milliseconds: created.pollIntervalMs.clamp(250, 5000),
    );
    final deadline = DateTime.now()
        .add(Duration(seconds: (created.expiresAt - _nowSeconds()).clamp(10, 900)));

    while (true) {
      await Future<void>.delayed(interval);
      onTick?.call();
      if (DateTime.now().isAfter(deadline)) {
        final expired = PairingProgress(
          phase: PairingPhase.failed,
          pairingId: created.pairingId,
          userCode: created.userCode,
          expiresAt: created.expiresAt,
          deviceName: deviceName,
          error: 'The pairing window closed before it was approved. Start again to get a '
              'new code.',
        );
        onProgress(expired);
        return expired;
      }

      final PairStatus status;
      try {
        status = await _poll(api, created.pairingId, provisioningSecret);
      } on EsiApiException catch (e) {
        // A single failed poll is not fatal: the phone may have moved between access
        // points. Keep trying until the window closes, but remember the last reason.
        progress = PairingProgress(
          phase: PairingPhase.awaitingApproval,
          pairingId: created.pairingId,
          userCode: created.userCode,
          expiresAt: created.expiresAt,
          approvalMethod: created.approval,
          deviceName: deviceName,
          message: 'Lost contact with the PC (${e.displayMessage}). Retrying…',
        );
        onProgress(progress);
        continue;
      } on EsiNetworkException catch (e) {
        progress = PairingProgress(
          phase: PairingPhase.awaitingApproval,
          pairingId: created.pairingId,
          userCode: created.userCode,
          expiresAt: created.expiresAt,
          approvalMethod: created.approval,
          deviceName: deviceName,
          message: 'Lost contact with the PC (${e.message}). Retrying…',
        );
        onProgress(progress);
        continue;
      }

      if (status.isPending) {
        if (confirm != null && !await confirm()) {
          final cancelled = PairingProgress(
            phase: PairingPhase.cancelled,
            pairingId: created.pairingId,
            userCode: created.userCode,
            expiresAt: created.expiresAt,
            deviceName: deviceName,
            error: 'Pairing was cancelled on the phone. The code ${created.userCode} is '
                'still waiting on the PC and expires on its own.',
          );
          onProgress(cancelled);
          return cancelled;
        }
        continue;
      }

      if (status.isDenied) {
        final denied = PairingProgress(
          phase: PairingPhase.failed,
          pairingId: created.pairingId,
          userCode: created.userCode,
          expiresAt: created.expiresAt,
          deviceName: deviceName,
          error: 'The request was denied on the PC. Nothing was changed.',
        );
        onProgress(denied);
        return denied;
      }

      if (status.isExpired) {
        final expired = PairingProgress(
          phase: PairingPhase.failed,
          pairingId: created.pairingId,
          userCode: created.userCode,
          expiresAt: created.expiresAt,
          deviceName: deviceName,
          error: 'The pairing code expired before it was approved.',
        );
        onProgress(expired);
        return expired;
      }

      if (status.isConsumed) {
        final consumed = PairingProgress(
          phase: PairingPhase.failed,
          pairingId: created.pairingId,
          userCode: created.userCode,
          expiresAt: created.expiresAt,
          deviceName: deviceName,
          error: 'These secrets were already collected by another device, so this pairing '
              'cannot be completed. Remove the pending request on the PC and start again.',
        );
        onProgress(consumed);
        return consumed;
      }

      if (!status.isApproved) {
        final failed = PairingProgress(
          phase: PairingPhase.failed,
          pairingId: created.pairingId,
          userCode: created.userCode,
          deviceName: deviceName,
          error: 'The PC reported an unexpected pairing state "${status.status}".',
        );
        onProgress(failed);
        return failed;
      }

      // ---- Approved: unwrap the one-time envelope (§5.4) ----------------------------
      onProgress(PairingProgress(
        phase: PairingPhase.decrypting,
        pairingId: created.pairingId,
        userCode: created.userCode,
        deviceName: deviceName,
        message: 'Approved. Opening the credentials…',
      ));

      final deviceId = status.deviceId;
      final envelopeJson = status.wrappedSecrets;
      if (deviceId == null || deviceId.isEmpty || envelopeJson == null) {
        final failed = PairingProgress(
          phase: PairingPhase.failed,
          pairingId: created.pairingId,
          deviceName: deviceName,
          error: 'The PC approved the pairing but did not release any credentials. This '
              'usually means the QR code did not carry the pairing secret.',
        );
        onProgress(failed);
        return failed;
      }

      try {
        final opened = await EsiCrypto.openPairingEnvelope(
          envelope: WrappedSecretEnvelope.fromJson(envelopeJson.json),
          provisioningSecret: provisioningSecret,
          pairingId: created.pairingId,
          deviceId: deviceId,
        );
        final done = PairingProgress(
          phase: PairingPhase.done,
          pairingId: created.pairingId,
          userCode: created.userCode,
          deviceName: deviceName,
          approval: PairingProgressApproval.complete,
          message: 'Paired. This phone is now device $deviceId.',
        );
        onProgress(done);
        return _withSecrets(done, deviceId, opened.masterSecret, opened.keyId);
      } on EsiCryptoException catch (e) {
        // The envelope is single-use and has already been claimed, so there is no retry.
        final failed = PairingProgress(
          phase: PairingPhase.failed,
          pairingId: created.pairingId,
          userCode: created.userCode,
          deviceName: deviceName,
          error: '${e.message} The credentials for this attempt have now been used, so '
              'pairing has to start again from the PC.',
        );
        onProgress(failed);
        return failed;
      }
    }
  }

  Future<PairRequestResult> _request(
    EsiApi api,
    String deviceName,
    String? osVersion,
    String? appVersion,
  ) async {
    // The pre-auth surface is not signed: everything under /api/v1/pair/ is on the
    // agent's public list and no key exists yet.
    final json = await api.postJson(
      '/pair/request',
      signed: false,
      body: <String, dynamic>{
        'deviceName': deviceName,
        'platform': 'android',
        'osVersion': ?osVersion,
        'appVersion': ?appVersion,
      },
    );
    return PairRequestResult.fromJson(json);
  }

  Future<PairStatus> _poll(
    EsiApi api,
    String pairingId,
    Uint8List provisioningSecret,
  ) async {
    final json = await api.getJson(
      '/pair/session',
      signed: false,
      query: <(String, String)>[
        ('id', pairingId),
        // §5.4: without this the agent approves but never releases the secrets.
        ('secret', EsiCrypto.base64(provisioningSecret)),
      ],
    );
    return PairStatus.fromJson(json);
  }

  static int _nowSeconds() => DateTime.now().millisecondsSinceEpoch ~/ 1000;

  static String _hostOf(EsiEndpoint endpoint) => endpoint.displayAddress;

  static PairingProgress _failure(String message) =>
      PairingProgress(phase: PairingPhase.failed, error: message);
}

/// [PairingProgress] after a successful unwrap. The secrets never enter the progress
/// object the UI renders; they are attached to the returned value only.
class PairingOutcome extends PairingProgress {
  const PairingOutcome({
    required super.phase,
    required this.deviceId,
    required this.masterSecret,
    required this.keyId,
    super.pairingId,
    super.userCode,
    super.expiresAt,
    super.approvalMethod,
    super.deviceName,
    super.approval = PairingProgressApproval.none,
    super.message,
    super.error,
  });

  final String deviceId;
  final Uint8List masterSecret;
  final int keyId;
}

/// Adds the secret fields without widening [PairingProgress] itself.
PairingProgress _withSecrets(
  PairingProgress base,
  String deviceId,
  Uint8List masterSecret,
  int keyId,
) =>
    PairingOutcome(
      phase: base.phase,
      deviceId: deviceId,
      masterSecret: masterSecret,
      keyId: keyId,
      pairingId: base.pairingId,
      userCode: base.userCode,
      expiresAt: base.expiresAt,
      approvalMethod: base.approvalMethod,
      deviceName: base.deviceName,
      approval: base.approval,
      message: base.message,
      error: base.error,
    );

/// Reads the unauthenticated handshake, used to confirm an address and show the agent's
/// reported pin before pairing. The pin it returns is a hint, never a pin to trust.
Future<Handshake?> readHandshake(EsiEndpoint endpoint) => readHandshakeUnpinned(endpoint);
