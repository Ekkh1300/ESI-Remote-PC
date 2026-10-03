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
  ///
  /// [provisioningSecret] is read on every poll rather than captured, because the secret
  /// does not exist when the attempt starts: the PC mints it and hands it out on a QR shown
  /// after the request. A phone that polls before scanning simply gets no credentials yet.
  Future<PairingProgress> pair({
    required EsiEndpoint endpoint,
    required String deviceName,
    required Uint8List Function() provisioningSecret,
    String? osVersion,
    String? appVersion,
    required void Function(PairingProgress) onProgress,
    Future<bool> Function()? confirm,
    void Function()? onTick,
  }) async {
    final api = _unpairedClient(endpoint);
    // The client has to outlive the request. It used to be closed in a `finally` wrapped
    // around the request alone, which left every poll after that running against a closed
    // client: each one failed with "Client is already closed", so the credentials could
    // never be collected no matter how correct the secret was.
    try {
      return await _run(
        api: api,
        endpoint: endpoint,
        deviceName: deviceName,
        provisioningSecret: provisioningSecret,
        osVersion: osVersion,
        appVersion: appVersion,
        onProgress: onProgress,
        confirm: confirm,
        onTick: onTick,
      );
    } finally {
      api.close();
    }
  }

  Future<PairingProgress> _run({
    required EsiApi api,
    required EsiEndpoint endpoint,
    required String deviceName,
    required Uint8List Function() provisioningSecret,
    String? osVersion,
    String? appVersion,
    required void Function(PairingProgress) onProgress,
    Future<bool> Function()? confirm,
    void Function()? onTick,
  }) async {
    // The agent stamps `expiresAt` with its own clock, so before asking for an attempt,
    // read its handshake for `serverTime` and measure how far that clock runs from ours.
    // That offset is what turns the agent's stamp into an instant this phone can act on.
    // Without it the pairing window becomes a function of the two machines agreeing, and on
    // a PC whose clock ran 110 s slow that function answered 10 seconds for a 120 second
    // window (D33). Best effort: if the handshake cannot be read we fall back to reading
    // `expiresAt` against our own clock, which is correct exactly when the clocks already
    // agree — this app's behaviour before the skew was measured.
    final handshake = await readHandshake(endpoint);
    // Measured on receipt rather than on send, which makes the offset an underestimate by
    // the response's travel time; a slightly late deadline is the safe side of the race.
    final agentSkew = handshake == null
        ? null
        : handshake.serverTime - (DateTime.now().millisecondsSinceEpoch ~/ 1000);

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
    }

    // When the agent will close this attempt, expressed in this phone's clock.
    final closesAtEpoch = closesAt(created.expiresAt, agentSkew);

    // Only a watchdog: when the attempt really closes is the agent's call — it stops at its
    // own `expiresAt` and answers `expired`, which the loop below treats as final. It used
    // to be `now > expiresAt` read straight off the agent's stamp, and `.clamp(10, 900)`
    // turned a 110 s disagreement between the two machines into a 10 second window: the
    // phone reported "The pairing window closed before it was approved" while the agent was
    // still holding the attempt open, waiting for someone to click Approve (D33). The margin
    // keeps it out of the race between the agent closing and our next poll noticing.
    final deadline = DateTime.fromMillisecondsSinceEpoch(
      deadlineEpoch(created.expiresAt, agentSkew) * 1000,
    );

    var progress = PairingProgress(
      phase: PairingPhase.awaitingApproval,
      pairingId: created.pairingId,
      userCode: created.userCode,
      expiresAt: closesAtEpoch,
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

    // The agent asks for one poll per second and holds the attempt until [closesAtEpoch].
    final interval = Duration(
      milliseconds: created.pollIntervalMs.clamp(250, 5000),
    );

    while (true) {
      await Future<void>.delayed(interval);
      onTick?.call();
      if (DateTime.now().isAfter(deadline)) {
        final expired = PairingProgress(
          phase: PairingPhase.failed,
          pairingId: created.pairingId,
          userCode: created.userCode,
          expiresAt: closesAtEpoch,
          deviceName: deviceName,
          error: 'The pairing window closed before it was approved. Start again to get a '
              'new code.',
        );
        onProgress(expired);
        return expired;
      }

      final PairStatus status;
      try {
        status = await _poll(api, created.pairingId, provisioningSecret());
      } on EsiApiException catch (e) {
        // A single failed poll is not fatal: the phone may have moved between access
        // points. Keep trying until the window closes, but remember the last reason.
        progress = PairingProgress(
          phase: PairingPhase.awaitingApproval,
          pairingId: created.pairingId,
          userCode: created.userCode,
          expiresAt: closesAtEpoch,
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
          expiresAt: closesAtEpoch,
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
            expiresAt: closesAtEpoch,
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
          expiresAt: closesAtEpoch,
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
          expiresAt: closesAtEpoch,
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
          expiresAt: closesAtEpoch,
          deviceName: deviceName,
          error: 'These secrets were already collected by another device, so this pairing '
              'cannot be completed. Remove the pending request on the PC and start again.',
        );
        onProgress(consumed);
        return consumed;
      }

      // Once the PC approves, a poll carrying no secret comes back "error", because §5.4
      // releases nothing to a phone that has not presented the one. That is not a failure:
      // the agent does not consume the attempt when the secret is missing, so this is the
      // cue to go and scan the QR, not a reason to give up. It only becomes fatal once the
      // phone has a secret and still gets nothing.
      if (status.status == 'error' && provisioningSecret().isEmpty) {
        final awaitingSecret = PairingProgress(
          phase: PairingPhase.awaitingApproval,
          pairingId: created.pairingId,
          userCode: created.userCode,
          expiresAt: closesAtEpoch,
          approvalMethod: created.approval,
          deviceName: deviceName,
          message: 'Approved on the PC, which is now holding the credentials until this '
              'phone shows it the one-time secret. Scan the QR on the PC, or paste it, to '
              'collect them.',
        );
        onProgress(awaitingSecret);
        continue;
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
          provisioningSecret: provisioningSecret(),
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
    // The parameter is omitted while the phone has not scanned the QR yet. An empty
    // `secret=` would be rejected outright as not decoding to 32 bytes, whereas leaving it
    // out is explicitly supported: the agent answers with the status and withholds the
    // credentials, and does not consume the attempt.
    final query = <(String, String)>[('id', pairingId)];
    if (provisioningSecret.isNotEmpty) {
      // §5.4: without this the agent approves but never releases the secrets.
      query.add(('secret', EsiCrypto.base64(provisioningSecret)));
    }

    final json = await api.getJson(
      '/pair/session',
      signed: false,
      query: query,
    );
    return PairStatus.fromJson(json);
  }

  /// Seconds of margin before this phone stops asking, so the watchdog cannot land in
  /// between the agent closing the attempt and our next poll hearing that it did.
  static const int _deadlineGraceSeconds = 30;

  /// The agent's `expiresAt` expressed in this phone's clock — the whole rule, in one
  /// place, so it can be tested without needing two clocks in the room.
  ///
  /// [agentSkew] is `serverTime - now` measured from the agent's handshake: positive when
  /// the agent's clock runs ahead of this phone's, negative when it lags. `null` means the
  /// skew could not be measured and the stamp is taken at face value, which is correct only
  /// when the two clocks already agree.
  static int closesAt(int expiresAt, int? agentSkew) =>
      agentSkew == null ? expiresAt : expiresAt - agentSkew;

  /// When this phone stops asking: when the agent will close the attempt, plus the
  /// [_deadlineGraceSeconds] margin that keeps the watchdog out of the race between the
  /// agent closing the attempt and our next poll hearing that it did.
  static int deadlineEpoch(int expiresAt, int? agentSkew) =>
      closesAt(expiresAt, agentSkew) + _deadlineGraceSeconds;

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
