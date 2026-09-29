namespace Esi.Core;

/// <summary>
/// The built-in notification rules.
/// <para>
/// Each rule is a threshold plus a sustain time. The sustain time is the part that
/// matters: without it, a single 95 % CPU sample during a game launch spams the phone, and
/// the user learns to ignore notifications, which defeats the purpose. A short cooldown
/// then prevents a sustained condition from producing one message per sample.
/// </para>
/// </summary>
public static class NotificationRuleCatalog
{
    public const string CpuHigh = "cpu.high";
    public const string CpuCritical = "cpu.critical";
    public const string GpuTempHigh = "gpu.temp.high";
    public const string GpuTempCritical = "gpu.temp.critical";
    public const string RamHigh = "ram.high";
    public const string DiskLow = "disk.low";
    public const string DiskCritical = "disk.critical";
    public const string BatteryLow = "battery.low";
    public const string AgentStarted = "agent.started";
    public const string AgentPairing = "agent.pairing";
    public const string SystemBoot = "system.boot";
    public const string SystemShutdown = "system.shutdown";
    public const string AppLaunched = "app.launched";
    public const string FileTransfer = "file.transfer";
    public const string GpuOverheat = "gpu.overheat";

    public static List<NotificationRuleDto> CreateDefaultRules() =>
    [
        new()
        {
            Id = CpuHigh, Enabled = true, BuiltIn = true, Condition = "cpu.usagePercent >= 90",
            Threshold = 90, SustainSeconds = 60, CooldownSeconds = 900, Severity = "warning",
            Title = "High CPU usage", Body = "CPU has been above 90% for a minute.",
        },
        new()
        {
            Id = CpuCritical, Enabled = true, BuiltIn = true, Condition = "cpu.usagePercent >= 98",
            Threshold = 98, SustainSeconds = 30, CooldownSeconds = 300, Severity = "critical",
            Title = "Critical CPU usage", Body = "CPU has been above 98% for 30 seconds.",
        },
        new()
        {
            Id = GpuTempHigh, Enabled = true, BuiltIn = true, Condition = "gpu.temperatureC >= 83",
            Threshold = 83, SustainSeconds = 30, CooldownSeconds = 900, Severity = "warning",
            Title = "GPU temperature is high", Body = "The GPU has been above 83°C for 30 seconds.",
        },
        new()
        {
            Id = GpuTempCritical, Enabled = true, BuiltIn = true, Condition = "gpu.temperatureC >= 90",
            Threshold = 90, SustainSeconds = 5, CooldownSeconds = 300, Severity = "critical",
            Title = "GPU temperature is critical", Body = "The GPU has reached 90°C. Check the cooling.",
        },
        new()
        {
            Id = RamHigh, Enabled = true, BuiltIn = true, Condition = "memory.usedPercent >= 92",
            Threshold = 92, SustainSeconds = 120, CooldownSeconds = 1800, Severity = "warning",
            Title = "Memory is nearly full", Body = "RAM usage has been above 92% for two minutes.",
        },
        new()
        {
            Id = DiskLow, Enabled = true, BuiltIn = true, Condition = "anyVolume.usedPercent >= 90",
            Threshold = 90, SustainSeconds = 0, CooldownSeconds = 21600, Severity = "warning",
            Title = "Low disk space", Body = "A drive is more than 90% full.",
        },
        new()
        {
            Id = DiskCritical, Enabled = true, BuiltIn = true, Condition = "anyVolume.usedPercent >= 97",
            Threshold = 97, SustainSeconds = 0, CooldownSeconds = 21600, Severity = "critical",
            Title = "Disk is critically full", Body = "A drive is more than 97% full.",
        },
        new()
        {
            Id = BatteryLow, Enabled = true, BuiltIn = true, Condition = "battery.chargePercent <= 20",
            Threshold = 20, SustainSeconds = 30, CooldownSeconds = 3600, Severity = "warning",
            Title = "Battery is low", Body = "The battery is at 20% or below.",
        },
        new()
        {
            Id = AgentStarted, Enabled = true, BuiltIn = true, Condition = "event:agent.started",
            SustainSeconds = 0, CooldownSeconds = 0, Severity = "info",
            Title = "PC is online", Body = "The ESI Remote PC agent started.",
        },
        new()
        {
            Id = AgentPairing, Enabled = true, BuiltIn = true, Condition = "event:pair.requested",
            SustainSeconds = 0, CooldownSeconds = 0, Severity = "info",
            Title = "New device wants to pair", Body = "Approve it on the PC, or run the approve command with the code.",
        },
        new()
        {
            Id = SystemBoot, Enabled = true, BuiltIn = true, Condition = "event:system.boot",
            SustainSeconds = 0, CooldownSeconds = 0, Severity = "info",
            Title = "PC started", Body = "Windows finished starting.",
        },
        new()
        {
            Id = SystemShutdown, Enabled = true, BuiltIn = true, Condition = "event:power.pending",
            SustainSeconds = 0, CooldownSeconds = 0, Severity = "info",
            Title = "PC is shutting down", Body = "A shutdown or restart was requested.",
        },
        new()
        {
            Id = AppLaunched, Enabled = false, BuiltIn = true, Condition = "event:app.launched",
            SustainSeconds = 0, CooldownSeconds = 0, Severity = "info",
            Title = "Application started", Body = "An app was launched from the phone.",
        },
        new()
        {
            Id = FileTransfer, Enabled = true, BuiltIn = true, Condition = "event:file.transfer",
            SustainSeconds = 0, CooldownSeconds = 0, Severity = "info",
            Title = "File transfer finished", Body = "A transfer completed.",
        },
    ];

    /// <summary>Preserves user customisations of built-in rules while adding new ones.</summary>
    public static List<NotificationRuleDto> Merge(List<NotificationRuleDto>? current)
    {
        var defaults = CreateDefaultRules();

        if (current is null || current.Count == 0)
            return defaults;

        var byId = current.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var merged = new List<NotificationRuleDto>(defaults.Count + current.Count);

        foreach (var rule in defaults)
        {
            // Keep the user's enabled/severity/threshold choices; take new fields from
            // the default so a rule that gains a sustain time behaves correctly.
            if (byId.Remove(rule.Id, out var existing))
            {
                rule.Enabled = existing.Enabled;
                rule.Severity = string.IsNullOrWhiteSpace(existing.Severity) ? rule.Severity : existing.Severity;
                rule.Title = string.IsNullOrWhiteSpace(existing.Title) ? rule.Title : existing.Title;
                rule.Body = existing.Body;
                rule.Threshold = existing.Threshold ?? rule.Threshold;
                if (existing.SustainSeconds > 0 || rule.SustainSeconds == 0)
                    rule.SustainSeconds = existing.SustainSeconds;
            }

            merged.Add(rule);
        }

        // Anything the user added by hand survives.
        merged.AddRange(byId.Values.Where(r => !r.BuiltIn));
        return merged;
    }
}

/// <summary>
/// A catalogue of well-known applications, pre-populated so a fresh install is useful
/// without hunting for executable paths.
/// <para>
/// Paths are candidates in documented default install locations plus the registry's
/// <c>App Paths</c> key, which is how Windows itself resolves these. Nothing here is
/// hard-coded as a single answer: a missing path simply shows as "not installed", and the
/// user can point the entry anywhere.
/// </para>
/// </summary>
public static class AppCatalog
{
    public sealed record Candidate(
        string Id,
        string Name,
        string Category,
        string[] Paths,
        string? ProcessName,
        string[]? Arguments = null)
    {
        /// <summary>Never null; a candidate simply has no arguments by default.</summary>
        public string[] EffectiveArguments => Arguments ?? [];
    }

    public static readonly Candidate[] Known =
    [
        new("chrome", "Google Chrome", "browser",
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe",
        ], "chrome", ["--start-maximized"]),

        new("firefox", "Mozilla Firefox", "browser",
        [
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe",
        ], "firefox"),

        new("edge", "Microsoft Edge", "browser",
        [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        ], "msedge"),

        new("steam", "Steam", "game",
        [
            @"C:\Program Files (x86)\Steam\steam.exe",
            @"C:\Program Files\Steam\steam.exe",
        ], "steam"),

        new("discord", "Discord", "communication",
        [
            @"%LOCALAPPDATA%\Discord\Update.exe",
            @"%LOCALAPPDATA%\Discord\app-*\Discord.exe",
        ], "Discord"),

        new("telegram", "Telegram", "communication",
        [
            @"%APPDATA%\Telegram Desktop\telegram.exe",
        ], "Telegram"),

        new("spotify", "Spotify", "media",
        [
            @"%APPDATA%\Spotify\Spotify.exe",
        ], "Spotify"),

        new("vlc", "VLC media player", "media",
        [
            @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
        ], "vlc"),

        new("spotify-music", "Spotify (Music)", "media", [], "Spotify"),

        new("explorer", "File Explorer", "system", ["explorer.exe"], "explorer"),
        new("cmd", "Command Prompt", "system", ["cmd.exe"], "cmd"),
        new("powershell", "PowerShell", "system",
            [@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"], "powershell"),
        new("terminal", "Windows Terminal", "system",
            [@"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe"], "WindowsTerminal"),
        new("notepad", "Notepad", "productivity",
            [@"%SystemRoot%\System32\notepad.exe"], "notepad"),
        new("taskmgr", "Task Manager", "system", ["taskmgr.exe"], "Taskmgr"),
        new("settings", "Windows Settings", "system", ["ms-settings:"], null),
        new("control", "Control Panel", "system", ["control.exe"], "control"),
        new("obs", "OBS Studio", "streaming",
        [
            @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
            @"C:\Program Files (x86)\obs-studio\bin\64bit\obs64.exe",
        ], "obs64"),
        new("vscode", "Visual Studio Code", "development",
        [
            @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe",
            @"C:\Program Files\Microsoft VS Code\Code.exe",
        ], "Code"),
    ];

    /// <summary>
    /// Builds the initial launcher configuration, resolving each candidate against the
    /// filesystem and the registry so pre-installed software is already wired up.
    /// </summary>
    public static List<AppEntryDto> CreateDefaultApps()
    {
        var apps = new List<AppEntryDto>();

        foreach (var candidate in Known)
        {
            var resolved = ResolvePath(candidate.Paths);
            var isShellTarget = resolved?.EndsWith(':') == true;

            apps.Add(new AppEntryDto
            {
                Id = candidate.Id,
                Name = candidate.Name,
                Category = candidate.Category,
                Path = resolved ?? Environment.ExpandEnvironmentVariables(candidate.Paths.FirstOrDefault() ?? string.Empty),
                Arguments = candidate.EffectiveArguments.ToList(),
                RunAs = "interactiveUser",
                Enabled = resolved is not null,
                BuiltIn = true,
                Pinned = candidate.Id is "steam" or "chrome" or "discord" or "spotify" or "explorer",
                ProcessName = candidate.ProcessName,
                Exists = resolved is not null,
            });
        }

        return apps;
    }

    /// <summary>Resolves an executable path, expanding environment variables and testing existence.</summary>
    public static string? ResolvePath(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);

            // Wildcard support for rolling-release installs (Discord's app-* folders).
            if (expanded.Contains('*', StringComparison.Ordinal))
            {
                var matches = SafeGlob(expanded);
                if (matches.Count > 0)
                    return matches[^1]; // newest folder sorts last
                continue;
            }

            // Shell targets such as ms-settings: are launchable but have no file on disk.
            if (expanded.EndsWith(':'))
                return expanded;

            if (File.Exists(expanded))
                return expanded;
        }

        // Fall back to the registry's App Paths, which is how Windows resolves them.
        return ResolveFromAppPaths(candidates);
    }

    private static string? ResolveFromAppPaths(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var exeName = Path.GetFileName(Environment.ExpandEnvironmentVariables(candidate));
            if (string.IsNullOrEmpty(exeName) || !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            {
                var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry32);
                if (root is null)
                    continue;

                using (root)
                {
                    using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                    if (key?.GetValue(null) is string path && File.Exists(path))
                        return path;
                }
            }
        }

        return null;
    }

    private static List<string> SafeGlob(string pattern)
    {
        try
        {
            var directory = Path.GetDirectoryName(pattern);
            var leaf = Path.GetFileName(pattern);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(leaf) || !Directory.Exists(directory))
                return [];

            var prefix = leaf.Split('*')[0];
            return Directory.EnumerateFiles(directory, leaf)
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
