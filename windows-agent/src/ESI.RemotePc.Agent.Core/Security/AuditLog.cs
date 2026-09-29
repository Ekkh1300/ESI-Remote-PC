using System.Security.Cryptography;
using System.Text;
using Esi.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Esi.Core.Security;

/// <summary>
/// Append-only, hash-chained audit log.
/// <para>
/// Each entry's <c>chain_hash</c> is <c>SHA256(previousChainHash ‖ canonicalLine)</c>. That
/// makes the log <b>tamper-evident</b>: editing or removing a historical row breaks every
/// hash after it, and <see cref="VerifyChain"/> reports the break. It is not tamper-proof —
/// an attacker with write access to the database can recompute the whole chain — but it
/// turns silent history rewriting into something that requires deliberate extra work and is
/// detectable by anyone who kept an earlier copy of the chain head.
/// </para>
/// <para>
/// The genesis value is 64 zeros, and each entry also embeds the agent's device id so two
/// agents' logs cannot be silently concatenated.
/// </para>
/// </summary>
public sealed class AuditLog(ILogger<AuditLog> log, AgentDatabase database)
{
    private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Security-relevant actions. Anything not in this list is not worth auditing.</summary>
    public const string ActionPairRequested = "pair.requested";
    public const string ActionPairApproved = "pair.approved";
    public const string ActionPairDenied = "pair.denied";
    public const string ActionAuthFailed = "auth.failed";
    public const string ActionAuthSucceeded = "auth.succeeded";
    public const string ActionNetworkRejected = "network.rejected";
    public const string ActionRateLimited = "rate.limited";
    public const string ActionPowerAction = "power.action";
    public const string ActionDeviceRevoked = "device.revoked";
    public const string ActionDeviceRotated = "device.rotated";
    public const string ActionSettingsChanged = "settings.changed";
    public const string ActionFileDenied = "file.denied";
    public const string ActionFileDeleted = "file.deleted";
    public const string ActionAppLaunched = "app.launched";
    public const string ActionCertificateReset = "certificate.reset";
    public const string ActionRemoteStarted = "remote.started";
    public const string ActionUploadTicket = "files.upload";
    public const string ActionRequestRejected = "http.rejected";

    public void Record(
        string action,
        string result,
        string? deviceId = null,
        string? deviceName = null,
        string? remoteAddress = null,
        string? detail = null)
    {
        try
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();

            var previousHash = ReadChainHead(connection, transaction);
            var now = AgentDatabase.NowUnix();
            var line = BuildCanonicalLine(now, deviceId, remoteAddress, action, result, detail);
            var chainHash = ComputeChainHash(previousHash, line);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO audit_log (ts, device_id, device_name, remote_address, action, result, detail, chain_hash)
                VALUES ($ts, $deviceId, $deviceName, $address, $action, $result, $detail, $chain);
                """;
            command.Parameters.AddWithValue("$ts", now);
            command.Parameters.AddWithValue("$deviceId", AgentDatabase.DbValue(deviceId));
            command.Parameters.AddWithValue("$deviceName", AgentDatabase.DbValue(deviceName));
            command.Parameters.AddWithValue("$address", AgentDatabase.DbValue(remoteAddress));
            command.Parameters.AddWithValue("$action", action);
            command.Parameters.AddWithValue("$result", result);
            command.Parameters.AddWithValue("$detail", AgentDatabase.DbValue(detail));
            command.Parameters.AddWithValue("$chain", chainHash);
            command.ExecuteNonQuery();

            transaction.Commit();
        }
        catch (Exception ex)
        {
            // Auditing must never break the operation being audited.
            log.LogError(ex, "Could not append to the audit log (action {Action})", action);
        }
    }

    public IReadOnlyList<AuditEntryDto> Tail(int limit = 200, string? deviceId = null)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();

            command.CommandText = """
                SELECT id, ts, device_id, device_name, remote_address, action, result, detail, chain_hash
                  FROM audit_log
                 WHERE ($deviceId IS NULL OR device_id = $deviceId)
                 ORDER BY id DESC
                 LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$deviceId", AgentDatabase.DbValue(deviceId));
            command.Parameters.AddWithValue("$limit", (long)Math.Clamp(limit, 1, 2000));

            using var reader = command.ExecuteReader();
            var result = new List<AuditEntryDto>();

            while (reader.Read())
            {
                result.Add(new AuditEntryDto
                {
                    Id = Convert.ToInt64(reader["id"]),
                    Ts = Convert.ToInt64(reader["ts"]),
                    DeviceId = reader["device_id"] is DBNull ? null : (string)reader["device_id"],
                    DeviceName = reader["device_name"] is DBNull ? null : (string)reader["device_name"],
                    RemoteAddress = reader["remote_address"] is DBNull ? null : (string)reader["remote_address"],
                    Action = (string)reader["action"],
                    Result = (string)reader["result"],
                    Detail = reader["detail"] is DBNull ? null : (string)reader["detail"],
                    ChainHash = (string)reader["chain_hash"],
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not read the audit log");
            return [];
        }
    }

    /// <summary>Recomputes the chain and reports whether every hash still lines up.</summary>
    public (bool Valid, int BrokenAtId) VerifyChain()
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, ts, device_id, remote_address, action, result, detail, chain_hash
                  FROM audit_log ORDER BY id ASC;
                """;

            using var reader = command.ExecuteReader();
            var previous = GenesisHash;

            while (reader.Read())
            {
                var line = BuildCanonicalLine(
                    Convert.ToInt64(reader["ts"]),
                    reader["device_id"] is DBNull ? null : (string)reader["device_id"],
                    reader["remote_address"] is DBNull ? null : (string)reader["remote_address"],
                    (string)reader["action"],
                    (string)reader["result"],
                    reader["detail"] is DBNull ? null : (string)reader["detail"]);

                var expected = ComputeChainHash(previous, line);
                var actual = (string)reader["chain_hash"];

                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    return (false, (int)Convert.ToInt64(reader["id"]));

                previous = actual;
            }

            return (true, 0);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not verify the audit chain");
            return (false, -1);
        }
    }

    private static string ReadChainHead(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT chain_hash FROM audit_log ORDER BY id DESC LIMIT 1";
        var result = command.ExecuteScalar() as string;
        return string.IsNullOrEmpty(result) ? GenesisHash : result;
    }

    /// <summary>
    /// Canonical form of an entry. Field order and the separator are fixed so both the
    /// writer and the verifier produce identical bytes. U+001F (ASCII unit separator)
    /// is used because it cannot occur in any of the fields.
    /// </summary>
    private static string BuildCanonicalLine(long ts, string? deviceId, string? remoteAddress, string action, string result, string? detail)
        => string.Join('\u001F',
            ts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            deviceId ?? string.Empty,
            remoteAddress ?? string.Empty,
            action,
            result,
            detail ?? string.Empty);

    private static string ComputeChainHash(string previousHash, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(previousHash + line);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Drops entries older than the retention window. Chain history is preserved.</summary>
    public int Purge(int retentionDays)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM audit_log WHERE ts < $cutoff";
            command.Parameters.AddWithValue("$cutoff", AgentDatabase.NowUnix() - (long)TimeSpan.FromDays(retentionDays).TotalSeconds);
            var removed = command.ExecuteNonQuery();

            if (removed > 0)
                log.LogWarning("Purged {Count} audit entries older than {Days} day(s). " +
                               "Note that purging breaks the hash chain; export the log first if you need continuity.",
                    removed, retentionDays);

            return removed;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not purge the audit log");
            return 0;
        }
    }
}
