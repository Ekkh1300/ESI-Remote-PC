using Esi.Core;
using Esi.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// Settings loading, and specifically what happens to a command-line override.
/// <para>
/// The bug these tests exist for was invisible to everything else in the suite. The
/// <c>--port</c> override reached the listener but not the settings the agent advertises,
/// so the agent bound one port and told every client - the handshake, the discovery reply,
/// the startup banner - to use another. On a normal install both are 48220, so it never
/// showed. The smoke test passed because it probes the port it asked for directly and never
/// asks the agent which port it thinks it is on. Only running the real app against the real
/// agent surfaced it.
/// </para>
/// </summary>
public sealed class SettingsStoreTests
{
    private static (SettingsStore Store, string Dir) NewStore(Action<AgentOptions>? arrange = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "esi-settings-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);

        var options = new AgentOptions { DataDirectory = dir, Version = "1.0.0" };
        arrange?.Invoke(options);

        return (new SettingsStore(NullLogger<SettingsStore>.Instance, options), dir);
    }

    /// <summary>
    /// The whole point: what the agent listens on and what it advertises must be one number.
    /// </summary>
    [Fact]
    public void A_port_override_survives_into_the_settings_the_agent_advertises()
    {
        var (store, dir) = NewStore(o => o.Settings.Port = 48999);

        try
        {
            Assert.Equal(48999, store.Current.Port);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The default when nothing asked for anything.</summary>
    [Fact]
    public void No_override_means_the_protocol_default()
    {
        var (store, dir) = NewStore();

        try
        {
            Assert.Equal(ProtocolConstants.DefaultHttpsPort, store.Current.Port);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>An explicit flag outranks a persisted file, which is the usual precedence.</summary>
    [Fact]
    public void A_port_override_outranks_a_settings_file()
    {
        var (store, dir) = NewStore(o => o.Settings.Port = 48999);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                """{ "port": 47000 }""");

            // A fresh store, because the first one already loaded.
            var second = new SettingsStore(
                NullLogger<SettingsStore>.Instance,
                new AgentOptions { DataDirectory = dir, Version = "1.0.0", Settings = { Port = 48999 } });

            Assert.Equal(48999, second.Current.Port);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// And a file with no port in it must not clobber the override either - the failure mode
    /// was a fresh DTO replacing the options wholesale, so any file at all was enough.
    /// </summary>
    [Fact]
    public void A_settings_file_without_a_port_leaves_the_override_alone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "esi-settings-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "mdnsEnabled": false }""");

            var store = new SettingsStore(
                NullLogger<SettingsStore>.Instance,
                new AgentOptions { DataDirectory = dir, Version = "1.0.0", Settings = { Port = 48999 } });

            Assert.Equal(48999, store.Current.Port);
            // A value the file did set still comes from the file: the merge is one-way.
            Assert.False(store.Current.MdnsEnabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>--http false is the only way to ask for plaintext, so it has to survive too.</summary>
    [Fact]
    public void An_explicit_plaintext_request_survives()
    {
        var (store, dir) = NewStore(o => o.Settings.Security.TlsEnabled = false);

        try
        {
            Assert.False(store.Current.Security.TlsEnabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>--debug must survive, and it turns on verbose logging as well.</summary>
    [Fact]
    public void The_debug_override_survives()
    {
        // DebugMode itself lives on AgentOptions, not on SettingsDto, so it is read straight
        // from the options by whoever needs it. What has to survive the settings load is the
        // pair of debug flags that do live in the settings.
        var (store, dir) = NewStore(o =>
        {
            o.DebugMode = true;
            o.Settings.Debug.Enabled = true;
            o.Settings.Debug.VerboseLogging = true;
        });

        try
        {
            Assert.True(store.Current.Debug.Enabled);
            Assert.True(store.Current.Debug.VerboseLogging);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A corrupt file still has to leave a usable agent behind. That was true before this
    /// change and must stay true.
    /// </summary>
    [Fact]
    public void A_corrupt_settings_file_falls_back_rather_than_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "esi-settings-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ this is not json");

            var store = new SettingsStore(
                NullLogger<SettingsStore>.Instance,
                new AgentOptions { DataDirectory = dir, Version = "1.0.0" });

            Assert.Equal(ProtocolConstants.DefaultHttpsPort, store.Current.Port);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The defaults still have to be filled in when nothing supplies them, or the agent
    /// starts with no file roots and no notification rules.
    /// </summary>
    [Fact]
    public void Defaults_are_still_applied_when_nothing_supplies_them()
    {
        var (store, dir) = NewStore();

        try
        {
            Assert.NotEmpty(store.Current.Files.AllowedRoots);
            Assert.NotEmpty(store.Current.Notifications.Rules);
            Assert.Equal(ProtocolConstants.DefaultDiscoveryPort, store.Current.DiscoveryPort);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
