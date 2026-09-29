using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Esi.Core.Storage;

/// <summary>
/// SQLite schema management and the connection factory.
/// <para>
/// SQLite is used rather than a server database because the agent must work on a machine
/// with nothing installed, and because the data is small and strictly local. WAL mode is
/// enabled so the metrics writer never blocks the HTTP request path, and every connection
/// gets a busy timeout because the Windows service and the CLI can briefly overlap.
/// </para>
/// </summary>
public sealed class AgentDatabase : IDisposable
{
    private readonly ILogger<AgentDatabase> _log;
    private readonly string _connectionString;
    private bool _initialised;

    public AgentDatabase(ILogger<AgentDatabase> log, AgentOptions options)
    {
        _log = log;

        var directory = options.ResolveDataDirectory();
        Directory.CreateDirectory(directory);

        DatabasePath = options.DatabasePath;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            // WAL lets the metrics writer and the API read concurrently.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    /// <summary>Creates the schema if it is absent. Safe to call on every start.</summary>
    public void EnsureCreated()
    {
        if (_initialised)
            return;

        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = Schema;
            command.ExecuteNonQuery();
            _initialised = true;
            _log.LogInformation("Database schema is ready at {Path}", DatabasePath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not initialise the database at {Path}", DatabasePath);
            throw;
        }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version     INTEGER NOT NULL,
            applied_at  INTEGER NOT NULL
        );

        -- The agent's own stable identity. Exactly one row, id = 1.
        CREATE TABLE IF NOT EXISTS agent_identity (
            id            INTEGER PRIMARY KEY CHECK (id = 1),
            device_id     TEXT    NOT NULL,
            device_name   TEXT    NOT NULL,
            created_at    INTEGER NOT NULL
        );

        -- Paired devices. The master secret is stored encrypted at rest (see DeviceStore).
        CREATE TABLE IF NOT EXISTS devices (
            device_id      TEXT    PRIMARY KEY,
            name           TEXT    NOT NULL,
            platform       TEXT    NOT NULL,
            scopes         INTEGER NOT NULL,
            enabled        INTEGER NOT NULL DEFAULT 1,
            first_paired   INTEGER NOT NULL,
            last_seen      INTEGER,
            last_address   TEXT,
            key_id         INTEGER NOT NULL,
            previous_key_id INTEGER,
            previous_key_valid_until INTEGER,
            master_secret  BLOB    NOT NULL,
            rotated_at     INTEGER,
            public_pin     TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_devices_last_seen ON devices (last_seen DESC);

        -- Pairing attempts awaiting human approval on the PC.
        CREATE TABLE IF NOT EXISTS pairing_attempts (
            pairing_id         TEXT    PRIMARY KEY,
            user_code          TEXT    NOT NULL,
            device_name        TEXT    NOT NULL,
            platform           TEXT    NOT NULL,
            os_version         TEXT,
            app_version        TEXT,
            remote_address     TEXT,
            status             TEXT    NOT NULL,
            created_at         INTEGER NOT NULL,
            expires_at         INTEGER NOT NULL,
            resolved_at        INTEGER,
            resolved_device_id TEXT,
            provisioning_secret BLOB,
            public_pin         TEXT,
            secrets_delivered  INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_pairing_expires ON pairing_attempts (expires_at);

        -- Single-use upload tickets for large file transfers.
        CREATE TABLE IF NOT EXISTS upload_tickets (
            ticket_id    TEXT    PRIMARY KEY,
            device_id    TEXT    NOT NULL,
            path         TEXT    NOT NULL,
            max_bytes    INTEGER NOT NULL,
            created_at   INTEGER NOT NULL,
            expires_at   INTEGER NOT NULL,
            consumed_at  INTEGER
        );

        -- Notifications raised by the rules engine and by agent events.
        CREATE TABLE IF NOT EXISTS notifications (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            kind        TEXT    NOT NULL,
            severity    TEXT    NOT NULL,
            title       TEXT    NOT NULL,
            body        TEXT,
            created_at  INTEGER NOT NULL,
            is_read     INTEGER NOT NULL DEFAULT 0,
            source      TEXT,
            device_id   TEXT,
            data        TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_notifications_created ON notifications (created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_notifications_unread ON notifications (is_read, created_at DESC);

        -- Append-only, hash-chained audit trail of every security-relevant action.
        CREATE TABLE IF NOT EXISTS audit_log (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            ts             INTEGER NOT NULL,
            device_id      TEXT,
            device_name    TEXT,
            remote_address TEXT,
            action         TEXT    NOT NULL,
            result         TEXT    NOT NULL,
            detail         TEXT,
            chain_hash     TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_audit_ts ON audit_log (ts DESC);
        CREATE INDEX IF NOT EXISTS ix_audit_device ON audit_log (device_id, ts DESC);

        -- Rule-evaluation state, so a sustained condition does not re-fire every sample.
        CREATE TABLE IF NOT EXISTS rule_state (
            rule_id             TEXT    PRIMARY KEY,
            condition_since     INTEGER,
            last_fired_at       INTEGER,
            suppress_until      INTEGER
        );

        -- Recently accessed files, for the "Recent" view in the file manager.
        CREATE TABLE IF NOT EXISTS recent_files (
            path          TEXT PRIMARY KEY,
            name          TEXT NOT NULL,
            is_directory  INTEGER NOT NULL,
            size_bytes    INTEGER NOT NULL,
            last_modified INTEGER,
            accessed_at   INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_recent_accessed ON recent_files (accessed_at DESC);

        -- Short-lived request metadata. Purged aggressively; it is security telemetry, not
        -- durable state.
        CREATE TABLE IF NOT EXISTS request_log (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            ts          INTEGER NOT NULL,
            method      TEXT    NOT NULL,
            path        TEXT    NOT NULL,
            device_id   TEXT,
            remote      TEXT,
            status      INTEGER NOT NULL,
            duration_ms INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_request_log_ts ON request_log (ts DESC);
        """;

    // ---- Small helpers used across the stores -------------------------------------

    /// <summary>
    /// Maps a nullable value onto the right SQLite parameter representation. A bare
    /// <c>(string?)DBNull.Value</c> cast does not compile because <see cref="DBNull"/> is
    /// not a string; these overloads keep the call sites readable.
    /// </summary>
    public static object DbValue(string? value) => value ?? (object)DBNull.Value;

    public static object DbValue(long? value) => value ?? (object)DBNull.Value;

    public static object DbValue(int? value) => value ?? (object)DBNull.Value;

    public static object DbValue(bool? value) => value is null ? DBNull.Value : value.Value ? 1L : 0L;

    public static object DbValue(byte[]? value) => value is null or { Length: 0 } ? DBNull.Value : value;

    public static string ToJson(object? value) => JsonSerializer.Serialize(value, ProtocolJson.Options);

    public static T? FromJson<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, ProtocolJson.Options);

    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static int ToDbBool(bool value) => value ? 1 : 0;

    /// <summary>Reads a SQLite INTEGER back into a bool.</summary>
    public static bool FromDbBool(object? value) => Convert.ToInt64(value) != 0;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
