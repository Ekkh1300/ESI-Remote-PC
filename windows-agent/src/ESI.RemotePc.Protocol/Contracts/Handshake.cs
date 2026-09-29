using System.Text.Json.Serialization;

namespace Esi.Protocol;

// ---------------------------------------------------------------------------------
// Handshake + pairing
// ---------------------------------------------------------------------------------

/// <summary>Response of <c>GET /api/v1/handshake</c>. Also the body of a discovery reply.</summary>
public sealed class HandshakeResponse
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = ProtocolConstants.Version;
    [JsonPropertyName("agentVersion")] public string AgentVersion { get; set; } = "0.0.0";
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("hostName")] public string HostName { get; set; } = "";
    [JsonPropertyName("httpsPort")] public int HttpsPort { get; set; } = ProtocolConstants.DefaultHttpsPort;
    [JsonPropertyName("discoveryPort")] public int DiscoveryPort { get; set; } = ProtocolConstants.DefaultDiscoveryPort;
    [JsonPropertyName("spkiSha256")] public string? SpkiSha256 { get; set; }
    [JsonPropertyName("tlsSubject")] public string? TlsSubject { get; set; }
    [JsonPropertyName("notBefore")] public long? NotBefore { get; set; }
    [JsonPropertyName("notAfter")] public long? NotAfter { get; set; }
    [JsonPropertyName("serverTime")] public long ServerTime { get; set; }
    [JsonPropertyName("pairingRequired")] public bool PairingRequired { get; set; } = true;
    [JsonPropertyName("pairingOpen")] public bool PairingOpen { get; set; } = true;
    [JsonPropertyName("maxPairedDevices")] public int MaxPairedDevices { get; set; } = 8;
    [JsonPropertyName("pairedDeviceCount")] public int PairedDeviceCount { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool> Features { get; set; } = new();
}

/// <summary>Body of <c>POST /api/v1/pair/request</c>.</summary>
public sealed class PairRequest
{
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "android";
    [JsonPropertyName("osVersion")] public string? OsVersion { get; set; }
    [JsonPropertyName("appVersion")] public string? AppVersion { get; set; }
    [JsonPropertyName("publicSpkiSha256")] public string? PublicSpkiSha256 { get; set; }
}

/// <summary>Body of <c>POST /api/v1/pair/request</c> response (HTTP 201).</summary>
public sealed class PairRequestResponse
{
    [JsonPropertyName("pairingId")] public string PairingId { get; set; } = "";
    [JsonPropertyName("userCode")] public string UserCode { get; set; } = "";
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("approval")] public string Approval { get; set; } = "desktop-prompt";
    // Defaulted from the protocol constant, not written as a literal: the rate-limit budget
    // for the route this tells a phone to poll is derived from the same constant, and two
    // copies of the number is how they drifted apart before.
    [JsonPropertyName("pollIntervalMs")] public int PollIntervalMs { get; set; } = ProtocolConstants.PairingPollIntervalMs;
}

/// <summary>Body of <c>GET /api/v1/pair/session</c>.</summary>
public sealed class PairStatusResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("keyId")] public int? KeyId { get; set; }
    [JsonPropertyName("spkiSha256")] public string? SpkiSha256 { get; set; }
    [JsonPropertyName("wrappedSecrets")] public WrappedSecretDto? WrappedSecrets { get; set; }
}

/// <summary>Base64 AES-GCM envelope.</summary>
public sealed class WrappedSecretDto
{
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = "";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
}

// ---------------------------------------------------------------------------------
// Identity / device administration
// ---------------------------------------------------------------------------------

public sealed class WhoAmIResponse
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("keyId")] public int KeyId { get; set; }
    [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = [];
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("firstPaired")] public long FirstPaired { get; set; }
    [JsonPropertyName("lastSeen")] public long? LastSeen { get; set; }
    [JsonPropertyName("lastAddress")] public string? LastAddress { get; set; }
    [JsonPropertyName("rotatedAt")] public long? RotatedAt { get; set; }
    [JsonPropertyName("agentVersion")] public string AgentVersion { get; set; } = "";
}

public sealed class PairedDeviceDto
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = [];
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("firstPaired")] public long FirstPaired { get; set; }
    [JsonPropertyName("lastSeen")] public long? LastSeen { get; set; }
    [JsonPropertyName("lastAddress")] public string? LastAddress { get; set; }
    [JsonPropertyName("keyId")] public int KeyId { get; set; }
    [JsonPropertyName("suspended")] public bool Suspended { get; set; }
}

public sealed class RotateRequest
{
    [JsonPropertyName("confirmDeviceId")] public string ConfirmDeviceId { get; set; } = "";
}

public sealed class RotateResponse
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("previousKeyId")] public int PreviousKeyId { get; set; }
    [JsonPropertyName("keyId")] public int KeyId { get; set; }
    [JsonPropertyName("graceSeconds")] public int GraceSeconds { get; set; } = ProtocolConstants.KeyRotationGraceSeconds;
    [JsonPropertyName("wrappedSecrets")] public WrappedSecretDto? WrappedSecrets { get; set; }
    /// <summary>Set when the agent could not wrap for an in-band client (remote admin rotation).</summary>
    [JsonPropertyName("requiresManualDelivery")] public bool RequiresManualDelivery { get; set; }
}
