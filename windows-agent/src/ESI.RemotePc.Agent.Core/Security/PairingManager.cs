using System.Security.Cryptography;
using System.Text;
using Esi.Core.Storage;
using Esi.Platform.Session;
using Microsoft.Data.Sqlite;

namespace Esi.Core.Security;

/// <summary>A pairing attempt in its current state.</summary>
public sealed record PairingAttempt(
    string PairingId,
    string UserCode,
    string DeviceName,
    string Platform,
    string? OsVersion,
    string? AppVersion,
    string? RemoteAddress,
    PairingStatus Status,
    long CreatedAt,
    long ExpiresAt,
    long? ResolvedAt,
    string? ResolvedDeviceId,
    byte[]? ProvisioningSecret,
    string? PublicPin,
    bool SecretsDelivered);

/// <summary>
/// Runs the pairing handshake.
/// <para>
/// The threat this design addresses: on a shared LAN, any device can reach the
/// unauthenticated pairing endpoint. Without human confirmation, an attacker could pair
/// silently and then shut the machine down. So the flow is:
/// </para>
/// <list type="number">
///   <item><description>The phone asks to pair and receives a <c>userCode</c>.</description></item>
///   <item><description>The PC shows a desktop prompt: "New device wants to pair", with the
///   device name and the code, and the user presses Approve or Deny.</description></item>
///   <item><description>On approval the agent mints a device id and a 32-byte master secret,
///   and encrypts them under a one-time <c>provisioningSecret</c> derived from the code the
///   <em>human physically transferred</em> (scanned QR or read aloud).</description></item>
///   <item><description>Only a phone that knows the provisioning secret can decrypt the
///   envelope, so a racing attacker who gets the approval popup cannot steal the
///   credentials.</description></item>
/// </list>
/// </summary>
public sealed class PairingManager(
    ILogger<PairingManager> log,
    AgentDatabase database,
    DeviceStore devices,
    SettingsStore settings,
    ISessionAgentBridge sessionAgent)
{
    /// <summary>Number of digits in the human-readable pairing code.</summary>
    private const int UserCodeDigits = 6;

    public event EventHandler<PairingAttempt>? PairingRequested;
    public event EventHandler<PairingAttempt>? PairingResolved;

    /// <summary>
    /// Raised the moment a phone holding the right provisioning secret takes its credentials -
    /// the point at which a pairing attempt has actually become a pairing. Approval is not
    /// that moment: a phone can be approved and still never collect (D36).
    /// </summary>
    public event EventHandler<PairingAttempt>? PairingCollected;

    /// <summary>
    /// The HTTPS port the agent listens on, used to build the polling URL.
    /// <para>
    /// Injected rather than read from settings so the URL is built from the port that is
    /// actually bound, which is not the configured one when the agent was started with
    /// <c>--port</c>. A QR carrying the wrong port is a pairing that silently never connects.
    /// </para>
    /// </summary>
    public int Port { get; set; } = ProtocolConstants.DefaultHttpsPort;

    public int PendingCount()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM pairing_attempts
             WHERE status = 'Pending' AND expires_at > $now;
            """;
        command.Parameters.AddWithValue("$now", AgentDatabase.NowUnix());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Creates a pairing attempt and asks the session agent to prompt the user.</summary>
    public (PairRequestResponse Response, string PairingId) Create(PairRequest request, string? remoteAddress)
    {
        var maxDevices = settings.Current.Security.MaxPairedDevices;
        if (devices.CountEnabled() >= maxDevices)
            throw new InvalidOperationException(
                $"The maximum number of paired devices ({maxDevices}) has been reached. Revoke one on the PC first.");

        var now = AgentDatabase.NowUnix();
        var window = Math.Clamp(settings.Current.Security.PairingWindowSeconds, 30, 900);
        var expiresAt = now + window;

        var attempt = new PairingAttempt(
            PairingId: EsiCrypto.NewDeviceId(),
            UserCode: GenerateUserCode(),
            DeviceName: Sanitise(request.DeviceName, 64),
            Platform: Sanitise(request.Platform, 32),
            OsVersion: request.OsVersion,
            AppVersion: request.AppVersion,
            RemoteAddress: remoteAddress,
            Status: PairingStatus.Pending,
            CreatedAt: now,
            ExpiresAt: expiresAt,
            ResolvedAt: null,
            ResolvedDeviceId: null,
            // Shown to the human and encoded in the QR; never leaves the PC in the clear.
            ProvisioningSecret: EsiCrypto.RandomBytes(ProtocolConstants.SecretLength),
            PublicPin: request.PublicSpkiSha256,
            SecretsDelivered: false);

        Insert(attempt);

        log.LogInformation("Pairing requested by {Device} ({Platform}) from {Address}; code {Code}, expires at {Expires}",
            attempt.DeviceName, attempt.Platform, remoteAddress, attempt.UserCode, expiresAt);

        // Ask the interactive session to prompt the user. If nobody is logged on, the
        // attempt stays pending and the user approves on the pairing page, or from the CLI.
        //
        // The polling URL - which carries the provisioning secret - is computed from the
        // *requesting* address, because that is the host the phone can already reach. Using
        // the machine's own idea of its LAN address would produce a code the phone cannot
        // resolve when the two are on different interfaces.
        if (sessionAgent.IsConnected)
        {
            var pollingUrl = BuildPollingUrl(attempt, remoteAddress ?? "localhost", Port);

            sessionAgent.ShowPairingPrompt(new PairingPromptRequest(
                attempt.PairingId,
                attempt.DeviceName,
                attempt.Platform,
                attempt.OsVersion,
                attempt.AppVersion,
                remoteAddress ?? "local",
                attempt.UserCode,
                window,
                pollingUrl));
        }
        else
        {
            log.LogInformation(
                "No interactive session is available for a prompt. Open {Url} on this PC to " +
                "transfer the pairing secret to the phone, then approve it with " +
                "'esirc pair approve {Code}' - which needs the service stopped, because the " +
                "page cannot approve: it is reachable by anything on the LAN and the code " +
                "is six digits.", BuildPollingUrl(attempt, "localhost", Port), attempt.UserCode);
        }

        PairingRequested?.Invoke(this, attempt);

        return (new PairRequestResponse
        {
            PairingId = attempt.PairingId,
            UserCode = attempt.UserCode,
            ExpiresAt = expiresAt,
            Approval = sessionAgent.IsConnected ? "desktop-prompt" : "browser-or-cli",
            PollIntervalMs = ProtocolConstants.PairingPollIntervalMs,
        }, attempt.PairingId);
    }

    public PairingAttempt? Get(string pairingId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE pairing_id = $id";
        command.Parameters.AddWithValue("$id", pairingId);
        return ReadOne(command);
    }

    public PairingAttempt? GetByUserCode(string userCode)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE user_code = $code";
        command.Parameters.AddWithValue("$code", userCode);
        return ReadOne(command);
    }

    /// <summary>
    /// The URL the phone polls once it knows the provisioning secret.
    /// <para>
    /// The secret is embedded in the query string rather than sent as a header because this
    /// URL is the payload of a QR code: a header has nowhere to go when the transfer is
    /// optical. It is a single-use secret with a five-minute lifetime, and the endpoint
    /// releases the credentials exactly once, so a URL that is shoulder-surfed or captured
    /// from a QR image afterwards is worthless.
    /// </para>
    /// </summary>
    public string BuildPollingUrl(PairingAttempt attempt, string host, int port)
    {
        if (attempt.ProvisioningSecret is null)
            throw new InvalidOperationException("That attempt has no provisioning secret to transfer.");

        var secret = EsiCrypto.Base64Url(attempt.ProvisioningSecret);
        var hostPart = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim();

        return $"https://{hostPart}:{port}{ProtocolConstants.ApiBase}/pair/session" +
               $"?id={Uri.EscapeDataString(attempt.PairingId)}" +
               $"&secret={Uri.EscapeDataString(secret)}" +
               $"&code={Uri.EscapeDataString(attempt.UserCode)}";
    }

    /// <summary>
    /// The URL behind the QR a PC shows when nothing is asking it for anything yet.
    /// <para>
    /// PROTOCOL.md §5.4 says the pairing QR carries the address, the device id, the pin and the
    /// one-time secret, and the app has a scanner written for exactly that shape. But the only
    /// QR the agent emitted came from <see cref="BuildPollingUrl"/>, which needs an attempt to
    /// exist — and an attempt can only be started from the phone, which will not send one
    /// until the form is filled, which needs the pin. So the scanner could never obtain the one
    /// field that is genuinely hard to type, and pairing a real phone came down to reading 44
    /// characters of base64 off a screen and typing them on a soft keyboard, where <c>l</c>,
    /// <c>I</c>, <c>1</c>, <c>0</c> and <c>O</c> are indistinguishable and autocorrect is one
    /// keystroke away. This is the other end of that loop: no attempt and therefore no secret,
    /// just what the phone needs before it is able to ask.
    /// </para>
    /// <para>
    /// Publishing the pin here gives an attacker on the LAN nothing they did not already have —
    /// it is in the unauthenticated <c>GET /handshake</c> by design, which is precisely why the
    /// app refuses to take it from there. What the QR adds is the out-of-band hop: nothing on
    /// the network path can alter the pixels on this screen, so the phone ends up comparing
    /// itself against what the user is actually looking at. See ROADMAP D35.
    /// </para>
    /// </summary>
    public string BuildConnectUrl(string host, string deviceId, string? spkiPin)
    {
        var hostPart = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim();

        var url = new StringBuilder("esiremotepc://pair?host=")
            .Append(Uri.EscapeDataString(hostPart))
            .Append("&port=").Append(Port)
            .Append("&deviceId=").Append(Uri.EscapeDataString(deviceId));

        // No TLS means no pin to publish, and the app asks for it by hand rather than being
        // handed an empty field it would silently accept.
        if (!string.IsNullOrWhiteSpace(spkiPin))
            url.Append("&pin=").Append(Uri.EscapeDataString(spkiPin));

        return url.ToString();
    }

    public IReadOnlyList<PairingAttempt> ListPending()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE status = 'Pending' AND expires_at > $now ORDER BY created_at";
        command.Parameters.AddWithValue("$now", AgentDatabase.NowUnix());

        using var reader = command.ExecuteReader();
        var result = new List<PairingAttempt>();
        while (reader.Read())
            result.Add(Map(reader));

        return result;
    }

    /// <summary>
    /// The reach a pairing grants. The desktop prompt and <c>esirc pair approve</c> hand a
    /// phone the same thing, so it is declared once here rather than spelled out at each of
    /// the two call sites that could otherwise drift apart.
    /// </summary>
    public const DeviceScope DefaultScopes = DeviceScope.Read | DeviceScope.Control | DeviceScope.Files;

    /// <summary>Approves a pairing attempt, creating the device and its secrets.</summary>
    public (PairedDevice Device, byte[] MasterSecret, int KeyId) Approve(string pairingId, DeviceScope scopes)
    {
        var attempt = Get(pairingId) ?? throw new InvalidOperationException("No such pairing attempt.");

        if (attempt.Status == PairingStatus.Approved)
            throw new InvalidOperationException("That pairing attempt has already been approved.");
        if (attempt.Status == PairingStatus.Denied)
            throw new InvalidOperationException("That pairing attempt was denied.");
        if (attempt.ExpiresAt < AgentDatabase.NowUnix())
        {
            Expire(pairingId);
            throw new InvalidOperationException("That pairing attempt has expired. Ask the phone to try again.");
        }

        var deviceId = EsiCrypto.NewDeviceId();
        var masterSecret = EsiCrypto.RandomBytes(ProtocolConstants.SecretLength);
        const int keyId = 1;

        var device = new PairedDevice(
            DeviceId: deviceId,
            Name: attempt.DeviceName,
            Platform: attempt.Platform,
            Scopes: scopes,
            Enabled: true,
            FirstPaired: AgentDatabase.NowUnix(),
            LastSeen: null,
            LastAddress: attempt.RemoteAddress,
            KeyId: keyId,
            MasterSecret: masterSecret,
            PreviousKeyId: null,
            PreviousKeyValidUntil: null,
            RotatedAt: null,
            PublicPin: attempt.PublicPin);

        devices.Upsert(device);
        Resolve(pairingId, PairingStatus.Approved, deviceId);

        log.LogInformation("Paired {Device} ({Platform}) as {DeviceId} with scopes {Scopes}",
            attempt.DeviceName, attempt.Platform, deviceId, scopes);

        PairingResolved?.Invoke(this, attempt with { Status = PairingStatus.Approved, ResolvedDeviceId = deviceId });

        return (device, masterSecret, keyId);
    }

    public void Deny(string pairingId)
    {
        var attempt = Get(pairingId);
        if (attempt is null)
            return;

        Resolve(pairingId, PairingStatus.Denied, null);
        log.LogInformation("Denied the pairing request from {Device} ({Address})", attempt.DeviceName, attempt.RemoteAddress);
        PairingResolved?.Invoke(this, attempt with { Status = PairingStatus.Denied });
    }

    public void Expire(string pairingId) => Resolve(pairingId, PairingStatus.Expired, null);

    private void Resolve(string pairingId, PairingStatus status, string? deviceId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pairing_attempts
               SET status = $status, resolved_at = $now, resolved_device_id = $deviceId
             WHERE pairing_id = $id;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$now", AgentDatabase.NowUnix());
        command.Parameters.AddWithValue("$deviceId", AgentDatabase.DbValue(deviceId));
        command.Parameters.AddWithValue("$id", pairingId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Polls an attempt on behalf of the phone. Once approved, the wrapped secrets are
    /// returned exactly once; a second poll reports <c>consumed</c>.
    /// </summary>
    public PairStatusResponse Poll(string pairingId, byte[]? provisioningSecret)
    {
        var attempt = Get(pairingId);

        if (attempt is null)
            return new PairStatusResponse { Status = "unknown" };

        if (attempt.Status == PairingStatus.Pending)
        {
            if (attempt.ExpiresAt < AgentDatabase.NowUnix())
            {
                Expire(pairingId);
                return new PairStatusResponse { Status = "expired" };
            }

            return new PairStatusResponse { Status = "pending" };
        }

        if (attempt.Status is PairingStatus.Denied or PairingStatus.Expired)
            return new PairStatusResponse { Status = attempt.Status.ToString().ToLowerInvariant() };

        if (attempt.Status != PairingStatus.Approved)
            return new PairStatusResponse { Status = "unknown" };

        if (attempt.SecretsDelivered)
            return new PairStatusResponse { Status = "consumed" };

        if (attempt.ResolvedDeviceId is null || attempt.ProvisioningSecret is null)
            return new PairStatusResponse { Status = "error" };

        if (provisioningSecret is null || provisioningSecret.Length != ProtocolConstants.SecretLength)
        {
            log.LogWarning("Pairing {PairingId} was polled without a valid provisioning secret; " +
                           "the secrets will not be released", pairingId);
            return new PairStatusResponse { Status = "error" };
        }

        // Claim the delivery before decrypting, so two concurrent polls cannot both win.
        if (!TryClaimSecrets(pairingId))
            return new PairStatusResponse { Status = "consumed" };

        var device = devices.Get(attempt.ResolvedDeviceId);
        if (device is null)
            return new PairStatusResponse { Status = "error" };

        var provisioningKey = EsiCrypto.DeriveProvisioningKey(provisioningSecret, pairingId);
        var plaintext = new byte[ProtocolConstants.SecretLength + 4];
        Buffer.BlockCopy(device.MasterSecret, 0, plaintext, 0, ProtocolConstants.SecretLength);
        Buffer.BlockCopy(EsiCrypto.BigEndianBytes(device.KeyId), 0, plaintext, ProtocolConstants.SecretLength, 4);

        var aad = Encoding.UTF8.GetBytes($"esi-pair-v1|{pairingId}|{device.DeviceId}");
        var wrapped = EsiCrypto.Seal(plaintext, provisioningKey, aad);

        log.LogInformation("Released the pairing secrets for {DeviceId} to {Device}", device.DeviceId, device.Name);

        // The phone is now paired, and the desktop prompt that has been waiting on it can go.
        // Nothing else in the service knows when this happens - only this method does - so the
        // announcement is raised here rather than left for a caller to remember.
        PairingCollected?.Invoke(this, attempt);

        return new PairStatusResponse
        {
            Status = "approved",
            DeviceId = device.DeviceId,
            KeyId = device.KeyId,
            WrappedSecrets = new WrappedSecretDto
            {
                Nonce = wrapped.NonceBase64,
                Ciphertext = wrapped.CiphertextBase64,
                Tag = wrapped.TagBase64,
            },
        };
    }

    private bool TryClaimSecrets(string pairingId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        // The WHERE clause is the claim: only one caller can flip 0 -> 1.
        command.CommandText = """
            UPDATE pairing_attempts
               SET secrets_delivered = 1
             WHERE pairing_id = $id AND secrets_delivered = 0 AND status = 'Approved';
            """;
        command.Parameters.AddWithValue("$id", pairingId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Rotates a device's master secret and returns the new wrapped envelope.</summary>
    public (PairedDevice Device, int PreviousKeyId, int NewKeyId, WrappedSecret Wrapped) Rotate(
        string deviceId,
        out bool inBandDelivery)
    {
        var device = devices.Get(deviceId) ?? throw new InvalidOperationException("No such paired device.");

        var newMaster = EsiCrypto.RandomBytes(ProtocolConstants.SecretLength);
        var newKeyId = device.KeyId + 1;
        var graceUntil = AgentDatabase.NowUnix() + ProtocolConstants.KeyRotationGraceSeconds;

        // Wrap under the CURRENT wrap key, because the client still holds the old secret.
        var wrapKey = EsiCrypto.DeriveWrapKey(device.MasterSecret, device.DeviceId);
        var plaintext = new byte[ProtocolConstants.SecretLength + 4];
        Buffer.BlockCopy(newMaster, 0, plaintext, 0, ProtocolConstants.SecretLength);
        Buffer.BlockCopy(EsiCrypto.BigEndianBytes(newKeyId), 0, plaintext, ProtocolConstants.SecretLength, 4);

        var aad = Encoding.UTF8.GetBytes($"esi-rotate-v1|{device.DeviceId}");
        var wrapped = EsiCrypto.Seal(plaintext, wrapKey, aad);

        devices.ApplyRotation(deviceId, newMaster, newKeyId, graceUntil, AgentDatabase.NowUnix());

        // The client can only unwrap this itself, so the new secret is not usable in-band.
        inBandDelivery = false;

        log.LogInformation("Rotated the keys for {DeviceId}: keyId {Old} -> {New}, previous valid for {Grace}s",
            deviceId, device.KeyId, newKeyId, ProtocolConstants.KeyRotationGraceSeconds);

        var updated = devices.Get(deviceId)!;
        return (updated, device.KeyId, newKeyId, wrapped);
    }

    /// <summary>Removes expired and consumed attempts. Called periodically.</summary>
    public int PurgeExpired(int olderThanSeconds = 3600)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM pairing_attempts
                 WHERE (status <> 'Pending' AND resolved_at IS NOT NULL AND resolved_at < $cutoff)
                    OR (status = 'Pending' AND expires_at < $now);
                """;
            command.Parameters.AddWithValue("$cutoff", AgentDatabase.NowUnix() - olderThanSeconds);
            command.Parameters.AddWithValue("$now", AgentDatabase.NowUnix());
            return command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not purge expired pairing attempts");
            return 0;
        }
    }

    // -----------------------------------------------------------------------------

    private void Insert(PairingAttempt attempt)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pairing_attempts
                (pairing_id, user_code, device_name, platform, os_version, app_version, remote_address,
                 status, created_at, expires_at, provisioning_secret, public_pin, secrets_delivered)
            VALUES
                ($id, $code, $name, $platform, $os, $app, $address,
                 $status, $created, $expires, $secret, $pin, 0);
            """;
        command.Parameters.AddWithValue("$id", attempt.PairingId);
        command.Parameters.AddWithValue("$code", attempt.UserCode);
        command.Parameters.AddWithValue("$name", attempt.DeviceName);
        command.Parameters.AddWithValue("$platform", attempt.Platform);
        command.Parameters.AddWithValue("$os", AgentDatabase.DbValue(attempt.OsVersion));
        command.Parameters.AddWithValue("$app", AgentDatabase.DbValue(attempt.AppVersion));
        command.Parameters.AddWithValue("$address", AgentDatabase.DbValue(attempt.RemoteAddress));
        command.Parameters.AddWithValue("$status", attempt.Status.ToString());
        command.Parameters.AddWithValue("$created", attempt.CreatedAt);
        command.Parameters.AddWithValue("$expires", attempt.ExpiresAt);
        command.Parameters.AddWithValue("$secret", attempt.ProvisioningSecret!);
        command.Parameters.AddWithValue("$pin", AgentDatabase.DbValue(attempt.PublicPin));
        command.ExecuteNonQuery();
    }

    private PairingAttempt? ReadOne(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static PairingAttempt Map(SqliteDataReader reader) => new(
        PairingId: (string)reader["pairing_id"],
        UserCode: (string)reader["user_code"],
        DeviceName: (string)reader["device_name"],
        Platform: (string)reader["platform"],
        OsVersion: reader["os_version"] is DBNull ? null : (string)reader["os_version"],
        AppVersion: reader["app_version"] is DBNull ? null : (string)reader["app_version"],
        RemoteAddress: reader["remote_address"] is DBNull ? null : (string)reader["remote_address"],
        Status: Enum.TryParse<PairingStatus>((string)reader["status"], out var status) ? status : PairingStatus.Pending,
        CreatedAt: Convert.ToInt64(reader["created_at"]),
        ExpiresAt: Convert.ToInt64(reader["expires_at"]),
        ResolvedAt: reader["resolved_at"] is DBNull ? null : Convert.ToInt64(reader["resolved_at"]),
        ResolvedDeviceId: reader["resolved_device_id"] is DBNull ? null : (string)reader["resolved_device_id"],
        ProvisioningSecret: reader["provisioning_secret"] is DBNull ? null : (byte[])reader["provisioning_secret"],
        PublicPin: reader["public_pin"] is DBNull ? null : (string)reader["public_pin"],
        SecretsDelivered: Convert.ToInt32(reader["secrets_delivered"]) != 0);

    private const string SelectSql = """
        SELECT pairing_id, user_code, device_name, platform, os_version, app_version, remote_address,
               status, created_at, expires_at, resolved_at, resolved_device_id, provisioning_secret,
               public_pin, secrets_delivered
          FROM pairing_attempts
        """;

    private static string GenerateUserCode()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string Sanitise(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown device";

        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? "Unknown device" : cleaned[..Math.Min(cleaned.Length, maxLength)];
    }
}
