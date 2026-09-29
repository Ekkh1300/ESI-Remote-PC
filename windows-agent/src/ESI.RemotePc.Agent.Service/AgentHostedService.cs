using System.Net;
using Esi.Platform.Capture;
using Esi.Platform.Machine;
using Esi.Platform.Power;
using Esi.Platform.Session;
using Esi.Platform.Telemetry;

namespace Esi.Service;

/// <summary>
/// Builds the sensor block of <c>/system/info</c>, always with provenance attached.
/// <para>
/// The rule this encodes: a value is either measured by something trustworthy, or it is
/// <c>null</c> with a note explaining what was missing. A fabricated temperature or a
/// "0 %" that means "I could not read it" is worse than an honest gap, because the user
/// cannot tell the difference.
/// </para>
/// </summary>
public sealed class GpuTelemetrySummary(TemperatureProvider temperatures, NvmlProvider nvml, SystemSampler sampler)
{
    public SensorSummary Summarise(MetricsResponse metrics)
    {
        var cpu = temperatures.CpuTemperature();
        var gpu = temperatures.GpuTemperature();
        var (fanRpm, fanNote) = temperatures.CpuFanRpm();
        var gpuFan = temperatures.GpuFanPercent();

        var summary = new SensorSummary
        {
            CpuTemperatureC = cpu.Celsius,
            GpuTemperatureC = gpu.Celsius,
            CpuFanRpm = fanRpm,
            GpuFanRpm = gpuFan,
            SensorsAvailable = [],
        };

        if (cpu.Celsius is not null)
        {
            summary.SensorsAvailable.Add("cpu.temperature");
            summary.Notes["cpu.temperature"] = cpu.Note ?? cpu.Source;
        }
        else
        {
            summary.Notes["cpu.temperature"] = cpu.Note ??
                "No trustworthy CPU temperature source is available on this machine.";
        }

        if (gpu.Celsius is not null)
        {
            summary.SensorsAvailable.Add("gpu.temperature");
            summary.Notes["gpu.temperature"] = gpu.Note ?? gpu.Source;
        }
        else
        {
            summary.Notes["gpu.temperature"] = gpu.Note ??
                "No GPU temperature source is available. Windows has no general-purpose GPU " +
                "temperature API: this is populated through NVML on NVIDIA cards, or a " +
                "hardware-monitor driver on other vendors.";
        }

        summary.Notes["cpu.fan"] = fanNote ?? string.Empty;
        summary.Notes["gpu.fan"] = gpuFan is null
            ? "No GPU fan speed is reported by NVML (cards without a fan report nothing)."
            : $"{gpuFan}% of maximum, from NVML.";

        summary.Notes["gpu.load"] = sampler.IsGpuEngineCounterAvailable
            ? "From the Windows \\GPU Engine(*) performance counters."
            : nvml.IsAvailable
                ? "From NVML."
                : "Unavailable: the \\GPU Engine(*) performance counter is not present and no vendor API is available.";

        return summary;
    }
}

/// <summary>
/// Owns the agent's background work: the metrics loop, the notification pump, discovery,
/// mDNS, the session-agent bridge, and periodic housekeeping.
/// </summary>
public sealed class AgentHostedService(
    ILogger<AgentHostedService> log,
    AgentOptions options,
    AgentDatabase database,
    SettingsStore settings,
    CertificateAuthority authority,
    LocalNetworkGuard guard,
    RateLimiter limiter,
    MetricsService metrics,
    NotificationEngine notifications,
    NotificationStore notificationStore,
    PairingManager pairing,
    DeviceStore devices,
    WebSocketHub hub,
    UdpDiscoveryResponder discovery,
    MdnsResponder mdns,
    HandshakeBuilder handshake,
    ISessionAgentBridge sessionAgent,
    GamingService gaming,
    AuditLog audit,
    Esi.Platform.Files.FileSandbox sandbox) : BackgroundService
{
    private readonly CancellationTokenSource _cts = new();
    private HandshakeResponse _cachedHandshake = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _cts.Token);
        var ct = linked.Token;

        try
        {
            Initialise();
            await RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("The agent is shutting down");
        }
        catch (Exception ex)
        {
            // A hosted service that throws stops the host. Log loudly, then exit non-zero
            // so the SCM's recovery actions restart the service, rather than appearing to
            // run while doing nothing.
            log.LogCritical(ex, "The agent's background loop failed. Exiting so the service can recover.");
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// Pushes the configured budgets into the limiter.
    /// <para>
    /// Split out of <see cref="Initialise"/> because the settings endpoint applies the same
    /// values at runtime, and having the mapping in two places is how the two drift apart.
    /// </para>
    /// </summary>
    private void ApplyRateLimits()
    {
        var security = settings.Current.Security;

        limiter.RequestsPerMinute = Math.Clamp(security.RateLimitPerMinute, 1, 100_000);
        limiter.UnauthenticatedPerMinute = Math.Clamp(security.UnauthenticatedPerMinute, 1, 100_000);
        limiter.UnauthenticatedBurst = Math.Clamp(security.UnauthenticatedBurst, 1, 100_000);
        limiter.PairingPerHour = Math.Clamp(security.PairingPerHour, 1, 10_000);

        // The burst never exceeds the sustained rate. A bucket that can hold more than the
        // minute can produce would hand out a burst no amount of waiting has earned.
        limiter.PairingBurst = Math.Min(limiter.PairingBurst, limiter.PairingPerHour);
        limiter.Burst = Math.Min(limiter.Burst, limiter.RequestsPerMinute);
    }

    private void Initialise()
    {
        database.EnsureCreated();
        guard.Configure(settings.Current.Security.LocalNetworkOnly, settings.Current.Security.AllowedSubnets);
        ApplyRateLimits();
        settings.ApplyToSandbox(sandbox);
        notifications.Prime(settings.Current.Notifications.Rules);

        _cachedHandshake = handshake.Build();

        // The certificate is touched once here so the pin exists before the first
        // discovery reply is sent, rather than being generated lazily mid-request.
        var certificate = authority.Certificate;

        log.LogInformation("ESI Remote PC agent {Version} ready", options.Version);
        log.LogInformation("  device id   : {DeviceId}", _cachedHandshake.DeviceId);
        log.LogInformation("  https port  : {Port} (TLS {Tls})", _cachedHandshake.HttpsPort, _cachedHandshake.SpkiSha256 is not null ? "on" : "off");
        log.LogInformation("  discovery   : udp/{Udp} mDNS {Mdns}", _cachedHandshake.DiscoveryPort, settings.Current.MdnsEnabled ? "on" : "off");
        log.LogInformation("  tls subject : {Subject}", authority.Subject ?? "(none)");
        log.LogInformation("  spki pin    : {Pin}", authority.SpkiPin ?? "(none)");
        log.LogInformation("  file roots  : {Roots}", string.Join(", ", sandbox.Roots.Select(r => r.CanonicalPath)));
        log.LogInformation("  paired      : {Paired}/{Max}", devices.CountEnabled(), settings.Current.Security.MaxPairedDevices);

        if (authority.SpkiPin is null && settings.Current.Security.TlsEnabled)
            log.LogError("TLS was requested but the certificate could not be created. " +
                         "Traffic will be plaintext; only request signing protects it.");

        notifications.RaiseEvent("system", "PC is online", "The ESI Remote PC agent started.");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var tasks = new List<Task>
        {
            Task.Run(() => StartSessionBridgeAsync(ct), ct),
            Task.Run(() => RunDiscoveryAsync(ct), ct),
            Task.Run(() => RunMetricsLoopAsync(ct), ct),
            Task.Run(() => RunHousekeepingAsync(ct), ct),
        };

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Starts the session-agent supervisor when the concrete bridge is in use.</summary>
    private Task StartSessionBridgeAsync(CancellationToken ct)
    {
        if (sessionAgent is SessionAgentBridge bridge)
            bridge.Start();

        return Task.CompletedTask;
    }

    private Task RunDiscoveryAsync(CancellationToken ct)
    {
        if (!settings.Current.DiscoveryEnabled)
        {
            log.LogInformation("UDP broadcast discovery is disabled by configuration");
            return Task.CompletedTask;
        }

        // The responder reads the handshake lazily, so a settings change is reflected on
        // the next probe without a restart.
        discovery.Start();

        if (settings.Current.MdnsEnabled)
        {
            mdns.Update(
                _cachedHandshake.DeviceName,
                MdnsResponder.SanitiseHostName(Environment.MachineName),
                settings.Current.Port,
                _cachedHandshake.DeviceId,
                _cachedHandshake.SpkiSha256,
                LocalAddresses());
            mdns.Start();
        }

        return Task.CompletedTask;
    }

    private static IPAddress[] LocalAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Where(a => !System.Net.IPAddress.IsLoopback(a))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async Task RunMetricsLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.Current.Metrics.SampleIntervalMs, 250, 10000));

        using var timer = new PeriodicTimer(interval);
        var first = true;

        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false))
        {
            try
            {
                // The first pass primes PDH baselines, so the loop deliberately waits one
                // extra tick before evaluating notification rules.
                var snapshot = await metrics.SampleNowAsync(interval, ct).ConfigureAwait(false);

                if (!first)
                {
                    notifications.Evaluate(snapshot);

                    if (snapshot.Gpu.Count > 0)
                        gaming.RecordSample(null);
                }

                first = false;
            }
            catch (Exception ex)
            {
                // A failed sample must not kill the loop; the next tick tries again.
                log.LogWarning(ex, "A metrics sample failed");
            }
        }
    }

    private async Task RunHousekeepingAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false))
        {
            try
            {
                var retention = Math.Clamp(settings.Current.Debug.LogRetentionDays, 1, 90);

                var purgedNotifications = notificationStore.Purge(
                    Math.Clamp(settings.Current.Notifications.RetentionDays, 1, retention));
                var purgedPairing = pairing.PurgeExpired();
                var purgedAudit = audit.Purge(retention);

                limiter.Sweep();

                // Keep the discovery identity current: the IP set can change when the
                // machine moves between networks.
                _cachedHandshake = handshake.Build();

                if (settings.Current.DiscoveryEnabled && settings.Current.MdnsEnabled && mdns.IsRunning)
                {
                    mdns.Update(
                        _cachedHandshake.DeviceName,
                        MdnsResponder.SanitiseHostName(Environment.MachineName),
                        settings.Current.Port,
                        _cachedHandshake.DeviceId,
                        _cachedHandshake.SpkiSha256,
                        LocalAddresses());
                }

                log.LogDebug("Housekeeping: purged {Notifications} notifications, {Pairing} pairing attempts, {Audit} audit rows",
                    purgedNotifications, purgedPairing, purgedAudit);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "A housekeeping pass failed");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();

        // The goodbye packets let network devices drop this service immediately instead of
        // waiting out the mDNS TTL.
        mdns.SendGoodbye();
        hub.CloseAll();

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _cts.Dispose();
        base.Dispose();
    }
}
