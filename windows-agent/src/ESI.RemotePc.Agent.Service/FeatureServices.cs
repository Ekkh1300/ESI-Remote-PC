using System.Security.Cryptography;
using Esi.Core.Storage;
using Esi.Platform.Apps;
using Esi.Platform.Capture;
using Esi.Platform.Machine;
using Esi.Platform.Power;
using Esi.Platform.Session;
using Microsoft.Data.Sqlite;

namespace Esi.Service;

/// <summary>Single-use, short-lived tickets that let a phone upload a file larger than the signed-body limit.</summary>
public sealed class UploadTicketStore(ILogger<UploadTicketStore> log, AgentDatabase database)
{
    public UploadTicketResponse Issue(string deviceId, string path, long size, long maxBytes)
    {
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size), "The declared size cannot be negative.");

        if (size > maxBytes)
            throw new InvalidOperationException($"That file is larger than the configured limit ({maxBytes / (1024 * 1024)} MB).");

        var ticketId = EsiCrypto.NewDeviceId();
        var now = AgentDatabase.NowUnix();
        var expiresAt = now + ProtocolConstants.UploadTicketSeconds;

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO upload_tickets (ticket_id, device_id, path, max_bytes, created_at, expires_at)
            VALUES ($ticket, $device, $path, $max, $now, $expires);
            """;
        command.Parameters.AddWithValue("$ticket", ticketId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$max", size);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expires", expiresAt);
        command.ExecuteNonQuery();

        PurgeExpired();

        return new UploadTicketResponse
        {
            TicketId = ticketId,
            ExpiresAt = expiresAt,
            MaxBytes = size,
            UploadUrl = $"/api/v1/files/upload?ticket={Uri.EscapeDataString(ticketId)}",
        };
    }

    /// <summary>
    /// Validates and consumes a ticket. Consumption is a conditional UPDATE, so two
    /// concurrent uploads with the same ticket cannot both succeed.
    /// </summary>
    public (bool Valid, string? DeviceId, string? Path, long MaxBytes, string? Error) TryConsume(string ticketId)
    {
        try
        {
            using var connection = database.Open();

            using var read = connection.CreateCommand();
            read.CommandText = """
                SELECT device_id, path, max_bytes, expires_at, consumed_at
                  FROM upload_tickets WHERE ticket_id = $ticket;
                """;
            read.Parameters.AddWithValue("$ticket", ticketId);

            string deviceId;
            string path;
            long maxBytes;

            using (var reader = read.ExecuteReader())
            {
                if (!reader.Read())
                    return (false, null, null, 0, "Unknown upload ticket.");

                if (reader["consumed_at"] is not DBNull)
                    return (false, null, null, 0, "That upload ticket has already been used.");

                var expires = Convert.ToInt64(reader["expires_at"]);
                if (expires < AgentDatabase.NowUnix())
                    return (false, null, null, 0, "That upload ticket has expired.");

                deviceId = (string)reader["device_id"];
                path = (string)reader["path"];
                maxBytes = Convert.ToInt64(reader["max_bytes"]);
            }

            using var claim = connection.CreateCommand();
            claim.CommandText = """
                UPDATE upload_tickets SET consumed_at = $now
                 WHERE ticket_id = $ticket AND consumed_at IS NULL;
                """;
            claim.Parameters.AddWithValue("$now", AgentDatabase.NowUnix());
            claim.Parameters.AddWithValue("$ticket", ticketId);

            if (claim.ExecuteNonQuery() != 1)
                return (false, null, null, 0, "That upload ticket has already been used.");

            return (true, deviceId, path, maxBytes, null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not validate the upload ticket {Ticket}", ticketId);
            return (false, null, null, 0, "The upload ticket could not be validated.");
        }
    }

    public int PurgeExpired()
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM upload_tickets WHERE expires_at < $cutoff OR consumed_at IS NOT NULL";
            command.Parameters.AddWithValue("$cutoff", AgentDatabase.NowUnix() - 600);
            return command.ExecuteNonQuery();
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>Configured launcher entries and the operations that act on them.</summary>
public sealed class AppService(
    ILogger<AppService> log,
    SettingsStore settings,
    AppLauncher launcher,
    NotificationEngine notifications,
    AuditLog audit)
{
    public AppListResponse List()
    {
        var apps = settings.Current.Apps;
        var categories = apps
            .Select(a => a.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Exists is computed live so an app that has been installed or uninstalled since
        // the settings were written shows the correct state.
        foreach (var app in apps)
        {
            app.Exists = !string.IsNullOrWhiteSpace(app.Path) &&
                         (app.Path.EndsWith(':') || File.Exists(Environment.ExpandEnvironmentVariables(app.Path)));
        }

        return new AppListResponse { Apps = apps, Categories = categories };
    }

    public LaunchAppResponse Launch(LaunchAppRequest request, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            var app = settings.Current.Apps.FirstOrDefault(a =>
                string.Equals(a.Id, request.Id, StringComparison.OrdinalIgnoreCase));

            if (app is null)
                return new LaunchAppResponse { Launched = false, Id = request.Id ?? "", Detail = "No such app." };

            if (!app.Enabled)
                return new LaunchAppResponse { Launched = false, Id = app.Id, Detail = "That app is disabled." };

            var spec = new AppLaunchSpec
            {
                Id = app.Id,
                Path = Environment.ExpandEnvironmentVariables(app.Path),
                Arguments = app.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(app.WorkingDirectory)
                    ? null
                    : Environment.ExpandEnvironmentVariables(app.WorkingDirectory),
                RunAs = app.RunAs,
            };

            var outcome = launcher.Launch(spec);
            if (outcome.Launched)
            {
                app.LastLaunched = AgentDatabase.NowUnix();
                audit.Record(AuditLog.ActionAppLaunched, "ok", deviceId, detail: app.Id);
                notifications.RaiseEvent("app", $"{app.Name} launched", $"Started {app.Name} on the PC.");
            }

            return new LaunchAppResponse
            {
                Launched = outcome.Launched,
                Id = app.Id,
                ProcessId = outcome.ProcessId,
                RunAs = outcome.RunAs,
                Detail = outcome.Detail,
            };
        }

        // Ad-hoc launch by path. Still sandboxed to executables that actually exist.
        if (string.IsNullOrWhiteSpace(request.Path))
            return new LaunchAppResponse { Launched = false, Detail = "Supply either an app id or a path." };

        var path = Environment.ExpandEnvironmentVariables(request.Path);
        if (!path.EndsWith(':') && !File.Exists(path))
            return new LaunchAppResponse { Launched = false, Detail = $"The executable does not exist: {path}" };

        var adHoc = launcher.Launch(new AppLaunchSpec
        {
            Id = "adhoc",
            Path = path,
            Arguments = request.Args ?? [],
            WorkingDirectory = request.WorkingDirectory,
            RunAs = "interactiveUser",
        });

        audit.Record(AuditLog.ActionAppLaunched, adHoc.Launched ? "ok" : "failed", deviceId, detail: path);

        return new LaunchAppResponse
        {
            Launched = adHoc.Launched,
            Id = "adhoc",
            ProcessId = adHoc.ProcessId,
            RunAs = adHoc.RunAs,
            Detail = adHoc.Detail,
        };
    }

    public RunningAppListResponse Running()
    {
        var processes = launcher.ListRunning();
        var apps = settings.Current.Apps;

        var result = new List<RunningAppDto>();
        foreach (var process in processes)
        {
            var match = apps.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.ProcessName) &&
                string.Equals(a.ProcessName, process.Name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                continue;

            result.Add(new RunningAppDto
            {
                AppId = match.Id,
                ProcessId = process.Pid,
                Name = process.Name,
                StartedAt = process.StartedAt?.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond,
                WorkingSetBytes = process.WorkingSet,
            });
        }

        return new RunningAppListResponse { Processes = result };
    }

    public TerminateResponse Terminate(string appId, bool force)
    {
        var app = settings.Current.Apps.FirstOrDefault(a =>
            string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));

        if (app is null)
            return new TerminateResponse { Terminated = 0, Forced = false };

        if (string.IsNullOrEmpty(app.ProcessName))
            return new TerminateResponse { Terminated = 0, Forced = false };

        var (terminated, forced) = launcher.TerminateByProcessName(app.ProcessName, force);
        log.LogInformation("Terminated {Count} process(es) matching {Name}{Forced}",
            terminated, app.ProcessName, forced ? " (forced)" : string.Empty);

        return new TerminateResponse { Terminated = terminated, Forced = forced };
    }
}

/// <summary>
/// Gaming mode: a power-scheme switch plus optional background-app suspension.
/// <para>
/// The previous state is captured before anything changes so that turning gaming mode off
/// restores exactly what was there before, rather than assuming "Balanced".
/// </para>
/// </summary>
public sealed class GamingService(
    ILogger<GamingService> log,
    SettingsStore settings,
    MetricsService metrics,
    NetworkProvider network,
    AppLauncher launcher,
    NotificationEngine notifications)
{
    private readonly object _gate = new();
    private bool _enabled;
    private string? _previousPowerScheme;
    private readonly List<string> _suspendedApps = [];
    private readonly List<int> _suspendedPids = [];
    private readonly List<GamingHistoryPoint> _history = [];
    private readonly Lock _historyLock = new();

    public const int MaxHistoryPoints = 600;

    public GamingStateResponse GetState()
    {
        var current = settings.Current.Gaming;

        if (!_enabled)
        {
            _enabled = current.Enabled;
        }

        var snapshot = metrics.GetAsync().GetAwaiter().GetResult();

        return new GamingStateResponse
        {
            Mode = _enabled ? "on" : "off",
            ProfileId = current.ProfileId,
            ActivePowerScheme = PowerStateProbe.GetActivePowerSchemeName(),
            AvailablePowerSchemes = PowerStateProbe.GetPowerSchemes().Select(s => s.Name).ToList(),
            CpuUsagePercent = snapshot.Cpu.UsagePercent,
            GpuUsagePercent = snapshot.GpuAggregate.UsagePercent,
            RamUsedPercent = snapshot.Memory.UsedPercent,
            GpuTemperatureC = snapshot.GpuAggregate.MaxTemperatureC,
            GpuMemoryUsedBytes = snapshot.Gpu.Count > 0 ? snapshot.Gpu.Max(g => g.MemoryUsedBytes ?? 0) : null,
            GpuMemoryTotalBytes = snapshot.Gpu.Count > 0 ? snapshot.Gpu.Max(g => g.MemoryTotalBytes ?? 0) : null,
            NetworkDownBytesPerSecond = snapshot.Network.Primary?.DownloadBytesPerSecond ?? 0,
            NetworkUpBytesPerSecond = snapshot.Network.Primary?.UploadBytesPerSecond ?? 0,
            PingMs = MeasurePing(),
            SuspendedApps = [.. _suspendedApps],
            LaunchAppId = current.LaunchAppId,
            SessionAgentConnected = false,
            History = SnapshotHistory(),
        };
    }

    public GamingModeResponse SetMode(GamingModeRequest request)
    {
        var warnings = new List<string>();
        var current = settings.Current.Gaming;

        lock (_gate)
        {
            if (request.Enabled && !_enabled)
            {
                // Capture before changing anything.
                _previousPowerScheme = PowerStateProbe.GetActivePowerSchemeGuid();

                var schemes = PowerStateProbe.GetPowerSchemes();
                var target = schemes.FirstOrDefault(s =>
                    s.Name.Contains(ProfileKeyword(request.ProfileId ?? current.ProfileId), StringComparison.OrdinalIgnoreCase));

                if (target.Guid is not null && !string.IsNullOrEmpty(target.Guid))
                {
                    if (PowerStateProbe.SetActivePowerScheme(target.Guid))
                    {
                        log.LogInformation("Gaming mode switched the power scheme to {Scheme}", target.Name);
                    }
                    else
                    {
                        warnings.Add($"Could not switch the power scheme to '{target.Name}'.");
                    }
                }
                else
                {
                    warnings.Add($"No power scheme matching '{request.ProfileId ?? current.ProfileId}' was found; the active one was kept.");
                }

                var suspendList = request.SuspendApps ?? current.SuspendApps;
                foreach (var appId in suspendList)
                {
                    var app = settings.Current.Apps.FirstOrDefault(a =>
                        string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));

                    if (app is null || string.IsNullOrEmpty(app.ProcessName))
                        continue;

                    // "Suspending" here means terminating the process; a real suspend would
                    // need the kernel's process-freeze, which is not a documented API. The
                    // previous state is restored by the user relaunching the app.
                    var running = launcher.ListRunning(app.ProcessName);
                    if (running.Count == 0)
                        continue;

                    var (terminated, _) = launcher.TerminateByProcessName(app.ProcessName);
                    if (terminated > 0)
                    {
                        _suspendedApps.Add(appId);
                        _suspendedPids.AddRange(running.Select(p => p.Pid));
                    }
                }

                if (_suspendedApps.Count > 0)
                    warnings.Add($"Closed {_suspendedApps.Count} background app(s) to free resources. They are not auto-restarted.");

                _enabled = true;
                current.Enabled = true;
                current.ProfileId = request.ProfileId ?? current.ProfileId;
            }
            else if (!request.Enabled && _enabled)
            {
                if (_previousPowerScheme is not null && PowerStateProbe.SetActivePowerScheme(_previousPowerScheme))
                {
                    log.LogInformation("Gaming mode restored the previous power scheme");
                }
                else
                {
                    warnings.Add("Could not restore the previous power scheme.");
                }

                if (_suspendedApps.Count > 0)
                {
                    warnings.Add("Suspended apps were not automatically restarted; launch them again from the Apps tab.");
                }

                _suspendedApps.Clear();
                _suspendedPids.Clear();
                _enabled = false;
                current.Enabled = false;
            }
        }

        settings.Current.Gaming.LaunchAppId = request.LaunchAppId ?? current.LaunchAppId;
        _ = settings.SaveAsync(settings.Current);

        if (_enabled)
            notifications.RaiseEvent("gaming", "Gaming mode is on", "The PC is now tuned for gaming.", NotificationSeverity.Info);

        return new GamingModeResponse
        {
            Enabled = _enabled,
            ProfileId = settings.Current.Gaming.ProfileId,
            PreviousPowerScheme = PowerSchemeNames.Resolve(_previousPowerScheme),
            SuspendedApps = [.. _suspendedApps],
            Warnings = warnings,
        };
    }

    public List<GamingProfileDto> Profiles()
    {
        var profiles = new List<GamingProfileDto>
        {
            new()
            {
                Id = "performance", Name = "Performance",
                Description = "Switches to the Ultimate Performance power plan when present.",
                PowerScheme = "Ultimate Performance", BuiltIn = true,
            },
            new()
            {
                Id = "balanced", Name = "Balanced",
                Description = "Keeps the standard Balanced plan. Nothing is changed.",
                PowerScheme = "Balanced", BuiltIn = true,
            },
            new()
        };

        foreach (var scheme in PowerStateProbe.GetPowerSchemes())
        {
            if (profiles.Any(p => string.Equals(p.PowerScheme, scheme.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            profiles.Add(new GamingProfileDto
            {
                Id = scheme.Guid,
                Name = scheme.Name,
                Description = "Uses the '" + scheme.Name + "' power plan.",
                PowerScheme = scheme.Name,
            });
        }

        return profiles;
    }

    public GamingStateResponse SetAppState(string appId, bool enabled, string? deviceId)
    {
        var app = settings.Current.Apps.FirstOrDefault(a =>
            string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));

        if (app is null)
            return GetState();

        app.Suspended = !enabled;
        _ = settings.SaveAsync(settings.Current);

        if (!enabled && !string.IsNullOrEmpty(app.ProcessName))
        {
            var (terminated, _) = launcher.TerminateByProcessName(app.ProcessName);
            if (terminated > 0)
            {
                _suspendedApps.Remove(appId);
                log.LogInformation("Gaming mode closed {App} ({Count} process(es))", appId, terminated);
            }
        }

        return GetState();
    }

    /// <summary>Best-effort presented-frame rate. Reported as null when it cannot be measured.</summary>
    public int? MeasureFps()
    {
        // There is no documented Win32 API for a system's presented frame rate. Reading the
        // display's current refresh rate is a lower bound, not the same thing, so it is
        // reported separately rather than dressed up as an FPS measurement.
        return null;
    }

    public void RecordSample(int? fps)
    {
        var snapshot = metrics.GetAsync().GetAwaiter().GetResult();

        lock (_historyLock)
        {
            _history.Add(new GamingHistoryPoint
            {
                Ts = AgentDatabase.NowUnix(),
                Fps = fps,
                Cpu = snapshot.Cpu.UsagePercent,
                Gpu = snapshot.GpuAggregate.UsagePercent,
                Temp = snapshot.GpuAggregate.MaxTemperatureC,
                Ping = MeasurePing(),
            });

            if (_history.Count > MaxHistoryPoints)
                _history.RemoveRange(0, _history.Count - MaxHistoryPoints);
        }
    }

    private List<GamingHistoryPoint> SnapshotHistory()
    {
        lock (_historyLock)
            return [.. _history];
    }

    private double? MeasurePing()
    {
        try
        {
            // A single ICMP echo to the default gateway, which is what a user means by
            // "ping" when they are looking at a network panel.
            var primary = network.GetPrimaryInterface();
            if (primary?.Gateway is null || !System.Net.IPAddress.TryParse(primary.Gateway, out var gateway))
                return null;

            using var client = new System.Net.NetworkInformation.Ping();
            var reply = client.Send(gateway, 1000);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success
                ? Math.Round((double)reply.RoundtripTime, 1)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ProfileKeyword(string profileId) => profileId switch
    {
        "performance" => "Ultimate Performance",
        "balanced" => "Balanced",
        _ => profileId,
    };
}

/// <summary>Resolves a power scheme GUID to its display name, if it is still present.</summary>
public static class PowerSchemeNames
{
    public static string? Resolve(string? guid) =>
        guid is null
            ? null
            : PowerStateProbe.GetPowerSchemes().FirstOrDefault(s => s.Guid == guid).Name;
}
