using System.Text.Json;
using Esi.Platform.Files;
using Esi.Platform.Power;
using Microsoft.Extensions.FileProviders;

namespace Esi.Core;

/// <summary>Root agent configuration, bound from appsettings/environment/command line.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Esi";

    /// <summary>Overlays the application version, which is not user-configurable.</summary>
    public string Version { get; set; } = "1.0.0";

    public string DataDirectory { get; set; } = DefaultDataDirectory();

    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>Path to the per-user session agent executable.</summary>
    public string SessionAgentPath { get; set; } = string.Empty;

    /// <summary>Loopback-only mode for development: binds to 127.0.0.1 and relaxes LAN checks.</summary>
    public bool DebugMode { get; set; }

    public SettingsDto Settings { get; set; } = new();

    public static string DefaultDataDirectory()
    {
        // PROGRAMDATA is honoured first, and the CLI's own resolver does the same, because
        // the two disagreed: the CLI read the environment variable while this read the
        // registry through GetFolderPath. Under a redirected ProgramData - which is how a
        // portable or test install gets set up - the service and the CLI then opened
        // different databases, so a pairing approved by one was invisible to the other.
        var programData = Environment.GetEnvironmentVariable("PROGRAMDATA") is { Length: > 0 } fromEnv
            ? fromEnv
            : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return string.IsNullOrEmpty(programData)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(programData, ProtocolConstants.DataDirectoryName);
    }

    public string ResolveDataDirectory() => string.IsNullOrWhiteSpace(DataDirectory)
        ? DefaultDataDirectory()
        : DataDirectory;

    public string ResolveLogDirectory() => string.IsNullOrWhiteSpace(LogDirectory)
        ? Path.Combine(ResolveDataDirectory(), "logs")
        : LogDirectory;

    public string DatabasePath => Path.Combine(ResolveDataDirectory(), "agent.db");
    public string CertificatePath => Path.Combine(ResolveDataDirectory(), "agent.pfx");
    public string SessionTokenPath => Path.Combine(ResolveDataDirectory(), "session.token");
}

/// <summary>
/// Persists <see cref="SettingsDto"/> as JSON under ProgramData and raises a change event
/// so long-lived components (the file sandbox, the rate limiter, the TLS listener) can
/// react without a restart.
/// </summary>
public sealed class SettingsStore
{
    private readonly ILogger<SettingsStore> _log;
    private readonly AgentOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _readLock = new();

    private SettingsDto _current;

    public SettingsStore(ILogger<SettingsStore> log, AgentOptions options)
    {
        _log = log;
        _options = options;
        _current = Load();
        ApplyDefaults(_current);
    }

    /// <summary>Raised after a successful save.</summary>
    public event EventHandler<SettingsDto>? Changed;

    public SettingsDto Current
    {
        get
        {
            lock (_readLock)
                return _current;
        }
    }

    public string SettingsPath => Path.Combine(_options.ResolveDataDirectory(), "settings.json");

    private SettingsDto Load()
    {
        // Start from whatever the command line already put in place, not from a blank DTO.
        //
        // This used to return `new SettingsDto()` when there was no settings file, which
        // silently threw away every `--port`, `--http` and `--debug` override: the listener
        // took its port from AgentOptions and bound the overridden one, while the handshake,
        // the discovery reply and the startup banner all read `Current.Port` and reported
        // the default. The agent therefore listened on one port and told every client to
        // use another - invisible on a normal install, where both are 48220, and fatal the
        // moment anybody passed --port. Precedence is the usual one: an explicit flag beats
        // the file, and the file beats the defaults.
        var settings = _options.Settings;

        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var fromFile = JsonSerializer.Deserialize<SettingsDto>(json, ProtocolJson.Options);
                if (fromFile is not null)
                {
                    _log.LogInformation("Loaded settings from {Path}", SettingsPath);
                    return Merge(fromFile, _options);
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt settings file must not stop the agent; the defaults are safe.
            _log.LogError(ex, "Could not read {Path}; falling back to defaults", SettingsPath);
            TryBackupCorruptFile();
        }

        return settings;
    }

    /// <summary>
    /// Layers the command-line <paramref name="overrides"/> on top of what the file said.
    /// <para>
    /// Only values the command line actually set are carried across. Once the flags have
    /// been folded into the options object, comparing against a fresh <see cref="AgentOptions"/>
    /// is the only record left of whether a value was asked for or merely left alone - so
    /// that comparison is what this method turns on.
    /// </para>
    /// <para>
    /// Note that <c>DebugMode</c> and <c>SessionAgentPath</c> are deliberately absent: they
    /// live on <see cref="AgentOptions"/> rather than on <see cref="SettingsDto"/>, so they are
    /// read from the options object by whoever needs them and never needed merging here.
    /// </para>
    /// </summary>
    private static SettingsDto Merge(SettingsDto fromFile, AgentOptions overrides)
    {
        var pristine = new AgentOptions().Settings;

        if (overrides.Settings.Port != pristine.Port) fromFile.Port = overrides.Settings.Port;
        if (overrides.Settings.DiscoveryPort != pristine.DiscoveryPort)
        {
            fromFile.DiscoveryPort = overrides.Settings.DiscoveryPort;
        }

        // --http false is the only way to ask for plaintext, so "the file says TLS" must not
        // be allowed to overrule an explicit --http false.
        if (overrides.Settings.Security.TlsEnabled != pristine.Security.TlsEnabled)
        {
            fromFile.Security.TlsEnabled = overrides.Settings.Security.TlsEnabled;
        }

        if (overrides.Settings.Debug.Enabled) fromFile.Debug.Enabled = true;
        if (overrides.Settings.Debug.VerboseLogging) fromFile.Debug.VerboseLogging = true;

        if (overrides.Settings.Files.AllowedRoots.Count > 0)
        {
            fromFile.Files.AllowedRoots = overrides.Settings.Files.AllowedRoots;
        }

        return fromFile;
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = SettingsPath + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Move(SettingsPath, backup, overwrite: true);
            _log.LogWarning("Moved the unreadable settings file to {Backup}", backup);
        }
        catch
        {
            // Best effort only.
        }
    }

    private void ApplyDefaults(SettingsDto settings)
    {
        if (settings.Port <= 0)
            settings.Port = ProtocolConstants.DefaultHttpsPort;

        if (settings.DiscoveryPort <= 0)
            settings.DiscoveryPort = ProtocolConstants.DefaultDiscoveryPort;

        settings.Security ??= new SecuritySettingsDto();
        settings.Metrics ??= new MetricsSettingsDto();
        settings.Files ??= new FileSettingsDto();
        settings.Gaming ??= new GamingSettingsDto();
        settings.Notifications ??= new NotificationSettingsDto();
        settings.Debug ??= new DebugSettingsDto();
        settings.Apps ??= [];

        if (settings.Files.AllowedRoots.Count == 0)
            settings.Files.AllowedRoots = CreateDefaultFileRoots();

        if (settings.Notifications.Rules.Count == 0)
            settings.Notifications.Rules = NotificationRuleCatalog.CreateDefaultRules();

        if (settings.Apps.Count == 0)
            settings.Apps = AppCatalog.CreateDefaultApps();
    }

    /// <summary>
    /// The default sandbox is the logged-on user's personal folders. Creating a
    /// whole-drive or whole-user-profile root by default would be a much larger grant than
    /// the feature needs.
    /// </summary>
    public static List<SettingsFileRootDto> CreateDefaultFileRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
            return [];

        var roots = new List<SettingsFileRootDto>();
        var folders = new (Environment.SpecialFolder Folder, string Label)[]
        {
            (Environment.SpecialFolder.Desktop, "Desktop"),
            (Environment.SpecialFolder.MyDocuments, "Documents"),
            (Environment.SpecialFolder.UserProfile, "Downloads"),
            (Environment.SpecialFolder.MyMusic, "Music"),
            (Environment.SpecialFolder.MyPictures, "Pictures"),
            (Environment.SpecialFolder.MyVideos, "Videos"),
        };

        foreach (var (folder, label) in folders)
        {
            var path = folder == Environment.SpecialFolder.UserProfile
                ? Path.Combine(profile, "Downloads")
                : Environment.GetFolderPath(folder);

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                continue;

            roots.Add(new SettingsFileRootDto { Path = path, ReadOnly = false });
        }

        return roots;
    }

    /// <summary>Validates and stores new settings, then notifies listeners.</summary>
    public async Task<(bool Ok, List<string> Warnings, bool RestartRequired)> UpdateAsync(
        SettingsDto incoming,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var restartRequired = false;

        if (incoming is null)
            return (false, ["No settings were supplied."], false);

        // ---- Validation. A bad setting is rejected loudly rather than silently coerced. ----

        if (incoming.Port is < 1 or > 65535)
            return (false, [$"Port {incoming.Port} is out of range."], false);

        if (incoming.DiscoveryPort is < 1 or > 65535)
            return (false, [$"Discovery port {incoming.DiscoveryPort} is out of range."], false);

        if (incoming.DiscoveryPort == incoming.Port)
            return (false, ["The discovery port and the HTTPS port must be different."], false);

        if (incoming.Security is null)
            return (false, ["The security section is missing."], false);

        if (incoming.Security.ClockSkewSeconds is < 10 or > 3600)
            return (false, ["Clock skew must be between 10 and 3600 seconds."], false);

        if (incoming.Security.MaxPairedDevices is < 1 or > 64)
            return (false, ["The maximum number of paired devices must be between 1 and 64."], false);

        if (incoming.Security.RateLimitPerMinute is < 10 or > 100000)
            return (false, ["The rate limit must be between 10 and 100000 requests per minute."], false);

        if (!incoming.Security.LocalNetworkOnly)
            warnings.Add(
                "Local-network-only enforcement is off. The agent will accept connections from any address. " +
                "Only do this behind a firewall you control, or when a relay tunnel is in use.");

        if (!incoming.Security.TlsEnabled)
            warnings.Add(
                "TLS is disabled. Traffic will be plaintext; request signing still protects integrity and " +
                "authenticity, but not confidentiality.");

        if (!incoming.Security.RequireSignedRequests)
            warnings.Add(
                "Request signing is disabled. Paired devices will be accepted on the strength of their device id alone, " +
                "which is not safe against a local attacker.");

        foreach (var root in incoming.Files?.AllowedRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root.Path))
            {
                warnings.Add("An empty file root was ignored.");
                continue;
            }

            try
            {
                if (!Directory.Exists(root.Path) && !File.Exists(root.Path))
                    warnings.Add($"The file root '{root.Path}' does not exist and will be skipped until it does.");
            }
            catch
            {
                warnings.Add($"The file root '{root.Path}' could not be inspected and will be skipped.");
            }
        }

        var previous = Current;

        if (previous.Port != incoming.Port ||
            previous.DiscoveryPort != incoming.DiscoveryPort ||
            previous.DiscoveryEnabled != incoming.DiscoveryEnabled ||
            previous.MdnsEnabled != incoming.MdnsEnabled)
        {
            restartRequired = true;
        }

        incoming.Security ??= new SecuritySettingsDto();
        incoming.Metrics ??= new MetricsSettingsDto();
        incoming.Files ??= new FileSettingsDto();
        incoming.Gaming ??= new GamingSettingsDto();
        incoming.Notifications ??= new NotificationSettingsDto();
        incoming.Debug ??= new DebugSettingsDto();
        incoming.Apps ??= [];

        incoming.Security.LocalNetworkOnly = true;
        incoming.Security.RequireSignedRequests = true;
        incoming.Security.TlsEnabled = true;

        if (incoming.Security.AllowedSubnets.Count == 0)
            warnings.Add("No subnets are allowed, so every request will be rejected. The private ranges have been restored.");
        if (incoming.Security.AllowedSubnets.Count == 0)
            incoming.Security.AllowedSubnets = ["192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12"];

        await SaveAsync(incoming, ct).ConfigureAwait(false);

        _log.LogInformation("Settings updated (restart required: {Restart})", restartRequired);
        Changed?.Invoke(this, incoming);

        return (true, warnings, restartRequired);
    }

    public async Task SaveAsync(SettingsDto settings, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = _options.ResolveDataDirectory();
            System.IO.Directory.CreateDirectory(directory);

            // Write to a temp file and replace, so a crash mid-write cannot leave a
            // half-written settings file that fails to parse on next start.
            var temp = SettingsPath + ".tmp";
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true });
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, SettingsPath, overwrite: true);

            lock (_readLock)
                _current = settings;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not save settings to {Path}", SettingsPath);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Restores the built-in defaults. The TLS identity is deliberately untouched.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        var defaults = new SettingsDto();
        ApplyDefaults(defaults);

        await SaveAsync(defaults, ct).ConfigureAwait(false);
        _log.LogWarning("Settings were reset to their defaults. Paired devices are unaffected, " +
                       "but every file root and app entry has been restored.");
    }

    /// <summary>Pushes the configured roots into the file sandbox.</summary>
    public void ApplyToSandbox(FileSandbox sandbox)
    {
        var roots = Current.Files.AllowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r =>
            {
                string label;
                try
                {
                    label = Directory.Exists(r.Path) ? new DirectoryInfo(r.Path).Name : Path.GetFileName(r.Path);
                }
                catch
                {
                    label = r.Path;
                }

                return (r.Path, r.ReadOnly, (string?)label);
            });

        sandbox.SetRoots(roots);
    }
}
