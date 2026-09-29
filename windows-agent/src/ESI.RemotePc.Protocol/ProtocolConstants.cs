namespace Esi.Protocol;

/// <summary>
/// Immutable protocol-level constants. Everything in this class is part of the
/// wire contract described in <c>docs/PROTOCOL.md</c>; changing a value here is a
/// breaking protocol change.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>Current wire protocol major version.</summary>
    public const int Version = 1;

    /// <summary>Marker used as the first line of every request signature canonical string.</summary>
    public const string SignatureDomain = "ESIV1";

    /// <summary>Default TCP port for the agent's HTTPS API.</summary>
    public const int DefaultHttpsPort = 48220;

    /// <summary>Default UDP port for broadcast discovery.</summary>
    public const int DefaultDiscoveryPort = 48211;

    /// <summary>Payload the client broadcasts to elicit a discovery reply.</summary>
    public const string DiscoveryProbe = "ESI_DISCOVER_V1";

    /// <summary>mDNS / DNS-SD service type advertised by the agent.</summary>
    public const string MdnsServiceType = "_esiremotepc._tcp.local.";

    /// <summary>mDNS domain.</summary>
    public const string MdnsDomain = "local.";

    /// <summary>Base path for the versioned REST surface.</summary>
    public const string ApiBase = "/api/v1";

    /// <summary>WebSocket endpoint for streaming (metrics, notifications, screen, input).</summary>
    public const string WebSocketPath = "/ws";

    /// <summary>Accepted clock skew between client timestamp and agent clock, in seconds.</summary>
    public const int DefaultClockSkewSeconds = 300;

    /// <summary>How long a previous key generation stays valid after a rotation, in seconds.</summary>
    public const int KeyRotationGraceSeconds = 120;

    /// <summary>Nonce length in bytes before base64url encoding.</summary>
    public const int NonceLength = 16;

    /// <summary>Master secret length in bytes.</summary>
    public const int SecretLength = 32;

    /// <summary>HKDF salt. Non-secret, fixed for the lifetime of protocol v1.</summary>
    public const string HkdfSalt = "ESI-RemotePC/v1";

    /// <summary>
    /// SHA-256 of the empty byte string, base64 encoded. Used as the value of
    /// <c>X-ESI-Content-Sha256</c> when a request carries no body.
    /// </summary>
    public const string EmptyBodySha256Base64 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=";

    /// <summary>Maximum request body size that participates in the signature.</summary>
    public const int MaxSignedBodyBytes = 8 * 1024 * 1024;

    /// <summary>Lifetime of a single-use file upload ticket, in seconds.</summary>
    public const int UploadTicketSeconds = 120;

    /// <summary>
    /// How often the agent tells a phone to poll <c>GET /api/v1/pair/session</c>.
    /// <para>
    /// A protocol constant rather than a setting, because two things depend on it agreeing:
    /// the value handed back in the <c>pair/request</c> response, and the rate-limit budget
    /// that endpoint is allowed to consume. While it was a literal <c>1000</c> written in
    /// two files, the rate limit was sized by hand and ended up below what the agent itself
    /// told phones to do, so a correctly-behaving client was throttled part-way through
    /// pairing and could not finish.
    /// </para>
    /// </summary>
    public const int PairingPollIntervalMs = 1000;

    /// <summary>Name of the service as installed in the Windows SCM.</summary>
    public const string ServiceName = "ESIRemotePcAgent";

    /// <summary>Name of the per-user session helper as installed in the Windows SCM.</summary>
    public const string SessionServiceName = "ESIRemotePcSession";

    /// <summary>Data directory name under <c>%ProgramData%</c>.</summary>
    public const string DataDirectoryName = "ESI-RemotePC";
}
