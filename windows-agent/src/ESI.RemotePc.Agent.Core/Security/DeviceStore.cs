using System.Security.Cryptography;
using System.Text;
using Esi.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Esi.Core.Security;

/// <summary>A paired device as stored, including the secrets the agent needs to verify it.</summary>
public sealed record PairedDevice(
    string DeviceId,
    string Name,
    string Platform,
    DeviceScope Scopes,
    bool Enabled,
    long FirstPaired,
    long? LastSeen,
    string? LastAddress,
    int KeyId,
    byte[] MasterSecret,
    int? PreviousKeyId,
    long? PreviousKeyValidUntil,
    long? RotatedAt,
    string? PublicPin)
{
    /// <summary>Whether the device has the given scope.</summary>
    public bool Has(DeviceScope scope) => (Scopes & scope) == scope;

    public bool CanRead => Has(DeviceScope.Read);
    public bool CanUseFiles => Has(DeviceScope.Files);
    public bool CanControl => Has(DeviceScope.Control);
    public bool IsAdmin => Has(DeviceScope.Admin);
}

/// <summary>
/// Stores paired devices and their master secrets.
/// <para>
/// The master secret is the root of trust for a device: every session key is derived from
/// it with HKDF, so compromising this row compromises exactly one device's access. It is
/// therefore encrypted at rest with DPAPI machine scope rather than stored in the clear,
/// which stops a casual file copy of <c>agent.db</c> from yielding usable credentials.
/// </para>
/// <para>
/// DPAPI machine scope is the right granularity here: the agent runs as LocalSystem and
/// the database is under ProgramData, so a different Windows account cannot decrypt it
/// even if it can read the file.
/// </para>
/// </summary>
public sealed class DeviceStore(ILogger<DeviceStore> log, AgentDatabase database)
{
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("ESI-RemotePC/v1/device-secret");

    public int Count()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM devices";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int CountEnabled()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM devices WHERE enabled = 1";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<PairedDevice> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectAllSql;
        command.CommandText += " ORDER BY first_paired DESC";

        using var reader = command.ExecuteReader();
        var result = new List<PairedDevice>();

        while (reader.Read())
            result.Add(Map(reader));

        return result;
    }

    public PairedDevice? Get(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectAllSql + " WHERE device_id = $id";
        command.Parameters.AddWithValue("$id", deviceId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Upsert(PairedDevice device)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices
                (device_id, name, platform, scopes, enabled, first_paired, last_seen, last_address,
                 key_id, previous_key_id, previous_key_valid_until, master_secret, rotated_at, public_pin)
            VALUES
                ($id, $name, $platform, $scopes, $enabled, $first, $lastSeen, $address,
                 $keyId, $prevKeyId, $prevUntil, $secret, $rotatedAt, $pin)
            ON CONFLICT(device_id) DO UPDATE SET
                name = excluded.name,
                platform = excluded.platform,
                scopes = excluded.scopes,
                enabled = excluded.enabled,
                last_seen = excluded.last_seen,
                last_address = excluded.last_address,
                key_id = excluded.key_id,
                previous_key_id = excluded.previous_key_id,
                previous_key_valid_until = excluded.previous_key_valid_until,
                master_secret = excluded.master_secret,
                rotated_at = excluded.rotated_at,
                public_pin = excluded.public_pin;
            """;

        command.Parameters.AddWithValue("$id", device.DeviceId);
        command.Parameters.AddWithValue("$name", device.Name);
        command.Parameters.AddWithValue("$platform", device.Platform);
        command.Parameters.AddWithValue("$scopes", (int)device.Scopes);
        command.Parameters.AddWithValue("$enabled", AgentDatabase.ToDbBool(device.Enabled));
        command.Parameters.AddWithValue("$first", device.FirstPaired);
        command.Parameters.AddWithValue("$lastSeen", device.LastSeen ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$address", AgentDatabase.DbValue(device.LastAddress));
        command.Parameters.AddWithValue("$keyId", device.KeyId);
        command.Parameters.AddWithValue("$prevKeyId", device.PreviousKeyId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$prevUntil", device.PreviousKeyValidUntil ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$secret", Protect(device.MasterSecret));
        command.Parameters.AddWithValue("$rotatedAt", device.RotatedAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$pin", AgentDatabase.DbValue(device.PublicPin));

        command.ExecuteNonQuery();
    }

    /// <summary>Records that a device was seen. Cheap enough to call on every request.</summary>
    public void TouchSeen(string deviceId, string? address, int keyId)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE devices
                   SET last_seen = $now, last_address = $address, key_id = $keyId
                 WHERE device_id = $id;
                """;
            command.Parameters.AddWithValue("$now", AgentDatabase.NowUnix());
            command.Parameters.AddWithValue("$address", AgentDatabase.DbValue(address));
            command.Parameters.AddWithValue("$keyId", keyId);
            command.Parameters.AddWithValue("$id", deviceId);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Telemetry must never fail a request that already authenticated.
            log.LogDebug(ex, "Could not update last-seen for {DeviceId}", deviceId);
        }
    }

    public bool Remove(string deviceId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM devices WHERE device_id = $id";
        command.Parameters.AddWithValue("$id", deviceId);
        return command.ExecuteNonQuery() > 0;
    }

    public bool SetEnabled(string deviceId, bool enabled)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET enabled = $enabled WHERE device_id = $id";
        command.Parameters.AddWithValue("$enabled", AgentDatabase.ToDbBool(enabled));
        command.Parameters.AddWithValue("$id", deviceId);
        return command.ExecuteNonQuery() > 0;
    }

    public bool SetScopes(string deviceId, DeviceScope scopes)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET scopes = $scopes WHERE device_id = $id";
        command.Parameters.AddWithValue("$scopes", (int)scopes);
        command.Parameters.AddWithValue("$id", deviceId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Applies a key rotation. The previous generation stays verifiable until
    /// <paramref name="graceUntil"/> so in-flight requests do not fail mid-rotation.
    /// </summary>
    public void ApplyRotation(string deviceId, byte[] newMasterSecret, int newKeyId, long graceUntil, long rotatedAt)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE devices
               SET master_secret = $secret,
                   key_id = $newKeyId,
                   previous_key_id = key_id,
                   previous_key_valid_until = $graceUntil,
                   rotated_at = $rotatedAt
             WHERE device_id = $id;
            """;
        command.Parameters.AddWithValue("$secret", Protect(newMasterSecret));
        command.Parameters.AddWithValue("$newKeyId", newKeyId);
        command.Parameters.AddWithValue("$graceUntil", graceUntil);
        command.Parameters.AddWithValue("$rotatedAt", rotatedAt);
        command.Parameters.AddWithValue("$id", deviceId);
        command.ExecuteNonQuery();
    }

    // ---- Secret protection --------------------------------------------------------

    private static byte[] Protect(byte[] secret)
    {
        try
        {
            return ProtectedData.Protect(secret, DpapiEntropy, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The device secrets could not be protected with DPAPI. This usually means the " +
                "database was copied from another machine.", ex);
        }
    }

    private static byte[] Unprotect(byte[] protectedSecret)
    {
        try
        {
            return ProtectedData.Unprotect(protectedSecret, DpapiEntropy, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "A stored device secret could not be decrypted with DPAPI on this machine. " +
                "This happens when agent.db was copied from another computer; the affected " +
                "device must be re-paired.", ex);
        }
    }

    private PairedDevice Map(SqliteDataReader reader)
    {
        var protectedSecret = (byte[])reader["master_secret"];
        byte[] masterSecret;

        try
        {
            masterSecret = Unprotect(protectedSecret);
        }
        catch (InvalidOperationException ex)
        {
            log.LogError(ex, "Device {DeviceId} cannot be authenticated on this machine and must re-pair",
                reader["device_id"]);
            masterSecret = [];
        }

        return new PairedDevice(
            DeviceId: (string)reader["device_id"],
            Name: (string)reader["name"],
            Platform: (string)reader["platform"],
            Scopes: (DeviceScope)Convert.ToInt32(reader["scopes"]),
            Enabled: Convert.ToInt32(reader["enabled"]) != 0,
            FirstPaired: Convert.ToInt64(reader["first_paired"]),
            LastSeen: reader["last_seen"] is DBNull ? null : Convert.ToInt64(reader["last_seen"]),
            LastAddress: reader["last_address"] is DBNull ? null : (string)reader["last_address"],
            KeyId: Convert.ToInt32(reader["key_id"]),
            MasterSecret: masterSecret,
            PreviousKeyId: reader["previous_key_id"] is DBNull ? null : Convert.ToInt32(reader["previous_key_id"]),
            PreviousKeyValidUntil: reader["previous_key_valid_until"] is DBNull ? null : Convert.ToInt64(reader["previous_key_valid_until"]),
            RotatedAt: reader["rotated_at"] is DBNull ? null : Convert.ToInt64(reader["rotated_at"]),
            PublicPin: reader["public_pin"] is DBNull ? null : (string)reader["public_pin"]);
    }

    private const string SelectAllSql = """
        SELECT device_id, name, platform, scopes, enabled, first_paired, last_seen, last_address,
               key_id, master_secret, previous_key_id, previous_key_valid_until, rotated_at, public_pin
          FROM devices
        """;
}
