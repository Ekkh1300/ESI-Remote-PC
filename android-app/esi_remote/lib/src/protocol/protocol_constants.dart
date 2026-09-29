/// Wire-protocol constants.
///
/// Every value here is a mirror of `windows-agent/src/ESI.RemotePc.Protocol/
/// ProtocolConstants.cs`. Changing any of them is a breaking protocol change, so the
/// file is deliberately a flat transcription rather than something configurable.
library;

class EsiProtocol {
  const EsiProtocol._();

  /// PROTOCOL_VERSION. A client MUST refuse to operate against anything else.
  static const int version = 1;

  /// First line of every request signature canonical string.
  static const String signatureDomain = 'ESIV1';

  static const int defaultHttpsPort = 48220;
  static const int defaultDiscoveryPort = 48211;

  /// `host:port` for a URL authority, always with the port spelled out.
  ///
  /// The port must never be dropped, even when it equals [defaultHttpsPort]: an `https`
  /// URL with no port means 443, and the agent does not listen on 443. Omitting it made
  /// the app unable to reach any agent on the only port a normal install uses.
  ///
  /// IPv6 literals are bracketed, because a second unbracketed colon would otherwise be
  /// read as the start of the port. A LAN is as likely to hand out a v6 address as not.
  static String authority(String host, int port) {
    final bracketed = host.contains(':') && !host.startsWith('[') ? '[$host]' : host;
    return '$bracketed:$port';
  }

  /// UDP payload broadcast to elicit a discovery reply.
  static const String discoveryProbe = 'ESI_DISCOVER_V1';

  static const String mdnsServiceType = '_esiremotepc._tcp.local.';
  static const String apiBase = '/api/v1';
  static const String webSocketPath = '/ws';

  static const int defaultClockSkewSeconds = 300;
  static const int keyRotationGraceSeconds = 120;
  static const int nonceLength = 16;
  static const int secretLength = 32;

  /// HKDF salt. Non-secret, fixed for the lifetime of protocol v1.
  static const String hkdfSalt = 'ESI-RemotePC/v1';

  /// SHA-256 of the empty byte string, base64. Used as `X-ESI-Content-Sha256` when there
  /// is no body. Verifying this constant against `sha256(<empty>)` in the test suite keeps
  /// the two from drifting apart silently.
  static const String emptyBodySha256B64 = '47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=';

  /// Bodies larger than this are not signed; large uploads use an upload ticket instead.
  static const int maxSignedBodyBytes = 8 * 1024 * 1024;

  static const int uploadTicketSeconds = 120;

  // --- Signature headers (PROTOCOL.md §4.1) -----------------------------------------

  static const String headerDeviceId = 'X-ESI-Device-Id';
  static const String headerKeyId = 'X-ESI-Key-Id';
  static const String headerTimestamp = 'X-ESI-Timestamp';
  static const String headerNonce = 'X-ESI-Nonce';
  static const String headerContentSha256 = 'X-ESI-Content-Sha256';
  static const String headerSignature = 'X-ESI-Signature';

  // --- Error codes (Protocol.ErrorCodes) ---------------------------------------------

  static const String errBadRequest = 'esi_bad_request';
  static const String errUnauthorized = 'esi_unauthorized';
  static const String errSignature = 'esi_signature';
  static const String errReplay = 'esi_replay';
  static const String errTimestamp = 'esi_timestamp';
  static const String errKeyId = 'esi_keyid';
  static const String errForbiddenNetwork = 'esi_forbidden_network';
  static const String errForbiddenScope = 'esi_forbidden_scope';
  static const String errNotFound = 'esi_not_found';
  static const String errRateLimited = 'esi_rate_limited';
  static const String errPathNotAllowed = 'esi_path_not_allowed';
  static const String errUnsupported = 'esi_unsupported';
  static const String errInternal = 'esi_internal';
  static const String errConflict = 'esi_conflict';
  static const String errUploadTooLarge = 'esi_upload_too_large';
  static const String errSessionAgentUnavailable = 'esi_session_agent_unavailable';
}

/// Power actions accepted by `POST /api/v1/power/{action}`.
enum PowerAction {
  shutdown('shutdown', 'Shut down'),
  restart('restart', 'Restart'),
  sleep('sleep', 'Sleep'),
  hibernate('hibernate', 'Hibernate'),
  lock('lock', 'Lock'),
  signout('signout', 'Sign out'),
  forceRestart('forceRestart', 'Force restart');

  const PowerAction(this.wire, this.label);

  /// Exactly the path segment the agent expects.
  final String wire;
  final String label;

  /// `lock` and `signout` are routed through the interactive session; if nobody is logged
  /// on, the agent refuses them rather than silently doing nothing.
  bool get needsInteractiveSession =>
      this == PowerAction.lock || this == PowerAction.signout;

  /// Waking is not a `/power/{action}` call. The phone sends the magic packet itself and
  /// only asks the agent whether that is likely to work.
  static PowerAction? get wake => null;
}

/// Media actions accepted by `POST /api/v1/media/{action}`.
enum MediaAction {
  play('play', 'Play'),
  pause('pause', 'Pause'),
  playPause('playPause', 'Play / pause'),
  stop('stop', 'Stop'),
  next('next', 'Next'),
  previous('previous', 'Previous'),
  volumeUp('volumeUp', 'Volume up'),
  volumeDown('volumeDown', 'Volume down'),
  volumeSet('volumeSet', 'Set volume'),
  mute('mute', 'Mute'),
  unmute('unmute', 'Unmute'),
  toggleMute('toggleMute', 'Toggle mute');

  const MediaAction(this.wire, this.label);

  final String wire;
  final String label;
}

/// Scopes the agent can grant a device. A missing scope greys out a feature rather than
/// producing a failing button.
enum EsiScope {
  read('read'),
  files('files'),
  control('control'),
  admin('admin');

  const EsiScope(this.wire);

  final String wire;

  static Set<EsiScope> parseAll(List<String> names) => names
      .map((n) => EsiScope.values.firstWhere(
            (s) => s.wire == n,
            orElse: () => EsiScope.read,
          ))
      .toSet();
}

/// Feature keys advertised in the handshake `features` map (PROTOCOL.md §10).
class EsiFeature {
  const EsiFeature._();

  static const String power = 'power';
  static const String metrics = 'metrics';
  static const String discovery = 'discovery';
  static const String wakeOnLan = 'wakeOnLan';
  static const String fileManager = 'fileManager';
  static const String appLauncher = 'appLauncher';
  static const String media = 'media';
  static const String remote = 'remote';
  static const String gaming = 'gaming';
  static const String notifications = 'notifications';
  static const String remoteAccess = 'remoteAccess';
}
