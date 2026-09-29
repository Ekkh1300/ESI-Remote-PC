using System.Text;
using Esi.Platform.Machine;
using Esi.Platform.Power;
using Esi.Platform.Session;

namespace Esi.Service;

/// <summary>
/// Owns the agent's own stable identity: the device id the phone pairs with, the display
/// name, and the handshake payload.
/// </summary>
public sealed class IdentityService(ILogger<IdentityService> log, AgentDatabase database, AgentOptions options)
{
    private readonly Lock _gate = new();
    private string _deviceId = "";
    private string _deviceName = "";
    private long _createdAt;

    public string DeviceId
    {
        get
        {
            EnsureLoaded();
            return _deviceId;
        }
    }

    public string DeviceName
    {
        get
        {
            EnsureLoaded();
            return _deviceName;
        }
    }

    public long CreatedAt
    {
        get
        {
            EnsureLoaded();
            return _createdAt;
        }
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_deviceId.Length > 0)
                return;

            using var connection = database.Open();

            using var read = connection.CreateCommand();
            read.CommandText = "SELECT device_id, device_name, created_at FROM agent_identity WHERE id = 1";
            using var reader = read.ExecuteReader();

            if (reader.Read())
            {
                _deviceId = (string)reader["device_id"];
                _deviceName = (string)reader["device_name"];
                _createdAt = Convert.ToInt64(reader["created_at"]);
            }
            else
            {
                // First run: mint a stable id and persist it. This id is what the phone
                // pins and what the mDNS advertisement carries, so it must not change on
                // restart.
                _deviceId = EsiCrypto.NewDeviceId();
                _deviceName = string.IsNullOrWhiteSpace(options.Settings.DeviceName)
                    ? CertificateAuthority.SafeMachineName()
                    : options.Settings.DeviceName!;
                _createdAt = AgentDatabase.NowUnix();

                using var write = connection.CreateCommand();
                write.CommandText = """
                    INSERT INTO agent_identity (id, device_id, device_name, created_at)
                    VALUES (1, $id, $name, $created);
                    """;
                write.Parameters.AddWithValue("$id", _deviceId);
                write.Parameters.AddWithValue("$name", _deviceName);
                write.Parameters.AddWithValue("$created", _createdAt);
                write.ExecuteNonQuery();

                log.LogInformation("Generated a new agent identity {DeviceId} ({DeviceName})", _deviceId, _deviceName);
            }
        }
    }

    /// <summary>Overrides the display name, for example from a settings change.</summary>
    public void SetDeviceName(string name)
    {
        lock (_gate)
        {
            _deviceName = name;
        }

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_identity SET device_name = $name WHERE id = 1";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }
}

/// <summary>Builds the handshake payload shared by HTTPS, UDP discovery and mDNS.</summary>
public sealed class HandshakeBuilder(
    IdentityService identity,
    SettingsStore settings,
    DeviceStore devices,
    CertificateAuthority authority,
    ISessionAgentBridge sessionAgent,
    AgentOptions options)
{
    public HandshakeResponse Build()
    {
        var current = settings.Current;
        var certificate = authority.Certificate;

        return new HandshakeResponse
        {
            ProtocolVersion = ProtocolConstants.Version,
            AgentVersion = options.Version,
            DeviceId = identity.DeviceId,
            DeviceName = identity.DeviceName,
            HostName = Environment.MachineName,
            HttpsPort = current.Port,
            DiscoveryPort = current.DiscoveryPort,
            SpkiSha256 = authority.SpkiPin,
            TlsSubject = authority.Subject,
            NotBefore = certificate?.NotBefore.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond,
            NotAfter = certificate?.NotAfter.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond,
            ServerTime = AgentDatabase.NowUnix(),
            PairingRequired = devices.CountEnabled() == 0,
            PairingOpen = devices.CountEnabled() < current.Security.MaxPairedDevices,
            MaxPairedDevices = current.Security.MaxPairedDevices,
            PairedDeviceCount = devices.CountEnabled(),
            Features = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["power"] = true,
                ["metrics"] = true,
                ["discovery"] = current.DiscoveryEnabled,
                ["mdns"] = current.MdnsEnabled,
                ["wakeOnLan"] = current.WakeOnLanEnabled,
                ["fileManager"] = current.Files.AllowedRoots.Count > 0,
                ["appLauncher"] = current.Apps.Count > 0,
                ["media"] = true,
                // Remote control needs the interactive session agent, so it is advertised
                // as unavailable rather than offered and then failing.
                ["remote"] = sessionAgent.IsConnected,
                ["gaming"] = true,
                ["notifications"] = true,
                ["remoteAccess"] = false, // deliberately not implemented in v1
            },
        };
    }
}
