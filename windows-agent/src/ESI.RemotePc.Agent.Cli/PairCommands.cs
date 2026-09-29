using System.Globalization;
using Esi.Cli;
using Esi.Core;
using Esi.Core.Security;
using Esi.Core.Storage;
using Esi.Platform.Session;
using Esi.Protocol;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Esi.Cli;

/// <summary>
/// Administrative commands that must work when the service is stopped: listing paired
/// devices, revoking one, approving a pending pairing from a console.
/// <para>
/// These open the same SQLite database the service uses, so they are only safe when the
/// service is not running. SQLite in WAL mode allows one writer and many readers, but
/// approving a pairing here while the service holds a pending attempt in memory would leave
/// the two disagreeing: the service would resolve the attempt to "denied" the moment the
/// phone polled. The CLI refuses rather than producing that.
/// </para>
/// </summary>
internal static class PairCommands
{
    public static int Show()
    {
        using var scope = BuildScope();
        var devices = scope.GetRequiredService<DeviceStore>();

        var list = devices.List();

        if (list.Count == 0)
        {
            Console.WriteLine("No devices are paired.");
            Console.WriteLine("Pair one from the phone: the app discovers this PC automatically, or scan the QR");
            Console.WriteLine("at https://localhost:" + ProtocolConstants.DefaultHttpsPort + "/pairing");
            return 0;
        }

        Console.WriteLine($"{list.Count} paired device(s):");
        Console.WriteLine();
        Console.WriteLine($"  {"DEVICE ID",-18} {"NAME",-20} {"SCOPES",-28} {"LAST SEEN",-14} STATE");
        Console.WriteLine($"  {new string('-', 16),-18} {new string('-', 18),-20} {new string('-', 26),-28} {new string('-', 12),-14} -----");

        foreach (var device in list)
        {
            var scopes = DescribeScopes(device.Scopes);
            var seen = device.LastSeen is { } ts ? FormatAge(DateTimeOffset.FromUnixTimeSeconds(ts)) : "never";
            var state = DescribeState(device);

            Console.WriteLine($"  {device.DeviceId,-18} {Trim(device.Name, 18),-20} {Trim(scopes, 26),-28} {seen,-14} {state}");

            if (device.LastAddress is { Length: > 0 } address)
                Console.WriteLine($"  {new string(' ', 18)} last seen from {address}, key #{device.KeyId}");
        }

        return 0;
    }

    public static int Revoke(string deviceId)
    {
        using var scope = BuildScope();
        var devices = scope.GetRequiredService<DeviceStore>();
        var audit = scope.GetRequiredService<AuditLog>();

        if (devices.Get(deviceId) is null)
        {
            Console.Error.WriteLine($"No device with id '{deviceId}' is paired. Run 'esirc pair show' to list them.");
            return 1;
        }

        if (devices.Remove(deviceId))
        {
            audit.Record(AuditLog.ActionDeviceRevoked, "ok", deviceId, detail: "revoked from the command line");
            Console.WriteLine($"Revoked {deviceId}.");
            Console.WriteLine("The phone stops working at its next request. Revoking twice is harmless.");
            return 0;
        }

        Console.Error.WriteLine($"The device {deviceId} could not be removed.");
        return 1;
    }

    /// <summary>
    /// Approves a pending pairing without a desktop prompt.
    /// <para>
    /// The escape hatch for the case that matters: the session agent is not running, so no
    /// prompt appeared, and the user is standing at the machine with a phone that is
    /// counting down. Approving from the console is an explicit, local, physical act, which
    /// is the same trust decision the prompt represents.
    /// </para>
    /// </summary>
    public static int Approve(string userCode)
    {
        var normalised = Normalise(userCode);

        using var scope = BuildScope();
        var pairing = scope.GetRequiredService<PairingManager>();
        var settings = scope.GetRequiredService<SettingsStore>();
        var devices = scope.GetRequiredService<DeviceStore>();
        var audit = scope.GetRequiredService<AuditLog>();

        var attempt = pairing.GetByUserCode(normalised);
        if (attempt is null)
        {
            Console.Error.WriteLine($"No pending pairing matches the code '{normalised}'.");
            Console.WriteLine("Pending attempts:");
            foreach (var pending in pairing.ListPending())
                Console.WriteLine($"  {pending.UserCode}  {pending.DeviceName} from {pending.RemoteAddress ?? "unknown"}");
            return 1;
        }

        var max = settings.Current.Security.MaxPairedDevices;
        if (devices.CountEnabled() >= max)
        {
            Console.Error.WriteLine($"The limit of {max} paired devices has been reached.");
            Console.WriteLine("Revoke one with 'esirc pair revoke <deviceId>' first.");
            return 1;
        }

        try
        {
            var (device, _, keyId) = pairing.Approve(attempt.PairingId, DeviceScope.Read | DeviceScope.Control | DeviceScope.Files);

            audit.Record(AuditLog.ActionPairApproved, "ok", device.DeviceId, device.Name,
                attempt.RemoteAddress, "approved from the command line");

            Console.WriteLine($"Approved {attempt.DeviceName} ({attempt.Platform}) from {attempt.RemoteAddress ?? "unknown"}.");
            Console.WriteLine($"  device id  {device.DeviceId}");
            Console.WriteLine($"  key id     {keyId}");
            Console.WriteLine();
            Console.WriteLine("The phone can now collect its credentials. The secrets are encrypted to the");
            Console.WriteLine("provisioning secret from the QR it scanned, so they are not shown here.");

            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static int Deny(string userCode)
    {
        using var scope = BuildScope();
        var pairing = scope.GetRequiredService<PairingManager>();
        var audit = scope.GetRequiredService<AuditLog>();

        var attempt = pairing.GetByUserCode(Normalise(userCode));
        if (attempt is null)
        {
            Console.Error.WriteLine($"No pending pairing matches the code '{Normalise(userCode)}'.");
            return 1;
        }

        pairing.Deny(attempt.PairingId);
        audit.Record(AuditLog.ActionPairDenied, "ok", remoteAddress: attempt.RemoteAddress,
            detail: $"denied from the command line: {attempt.DeviceName}");

        Console.WriteLine($"Denied the pairing request from {attempt.DeviceName}.");
        return 0;
    }

    public static int Pending()
    {
        using var scope = BuildScope();
        var pairing = scope.GetRequiredService<PairingManager>();
        var pending = pairing.ListPending();

        if (pending.Count == 0)
        {
            Console.WriteLine("No pairing requests are waiting.");
            return 0;
        }

        Console.WriteLine($"{pending.Count} pending pairing request(s):");
        foreach (var attempt in pending)
        {
            var remaining = attempt.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Console.WriteLine($"  {attempt.UserCode}  {Trim(attempt.DeviceName, 20),-20} {attempt.Platform,-9} {attempt.RemoteAddress ?? "unknown",-16} expires in {remaining}s");
        }

        Console.WriteLine();
        Console.WriteLine("Approve with: esirc pair approve <code>");

        return 0;
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Builds a container over the agent's own services.
    /// <para>
    /// The CLI reuses <see cref="AgentDatabase"/> and <see cref="PairingManager"/> rather
    /// than reimplementing the schema or the pairing state machine. That is the point: two
    /// implementations of a credential store are two chances to get the security wrong, and
    /// only one of them would get the attention.
    /// </para>
    /// </summary>
    private static ServiceProvider BuildScope()
    {
        var services = new ServiceCollection();

        // No logging provider is registered. The CLI already prints its own diagnostics, and
        // a stray provider would interleave framework warnings into that output.
        services.AddLogging();

        var options = new AgentOptions { DataDirectory = ServiceManager.ResolveDataDirectory() };
        services.AddSingleton(options);
        services.AddSingleton<AgentDatabase>();
        services.AddSingleton<DeviceStore>();
        services.AddSingleton<AuditLog>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SessionAgentBridge>();
        services.AddSingleton<ISessionAgentBridge>(provider => provider.GetRequiredService<SessionAgentBridge>());
        services.AddSingleton<PairingManager>();

        var provider = services.BuildServiceProvider();

        // The schema is created on demand so `pair show` works before the service has ever run.
        provider.GetRequiredService<AgentDatabase>().EnsureCreated();

        return provider;
    }

    /// <summary>
    /// Accepts the code with or without the space the prompt shows it with, in either case.
    /// <para>
    /// A user reading a code off a phone screen and typing it into a console will not
    /// reproduce the formatting the UI chose, and failing on "123 456" when "123456" was
    /// shown would be pure friction.
    /// </para>
    /// </summary>
    private static string Normalise(string code)
        => new string(code.Where(char.IsAsciiDigit).ToArray());

    private static string DescribeScopes(DeviceScope scopes)
    {
        var parts = new List<string>(5);

        if (scopes.HasFlag(DeviceScope.Read)) parts.Add("read");
        if (scopes.HasFlag(DeviceScope.Control)) parts.Add("control");
        if (scopes.HasFlag(DeviceScope.Files)) parts.Add("files");
        if (scopes.HasFlag(DeviceScope.Admin)) parts.Add("admin");

        return parts.Count == 0 ? "none" : string.Join(',', parts);
    }

    /// <summary>
    /// A device is "suspended" for the duration of a key rotation's grace window: its old
    /// key still works, the new one is already active, and a phone holding the old key would
    /// otherwise look like an attacker.
    /// </summary>
    private static string DescribeState(PairedDevice device)
    {
        if (!device.Enabled)
            return "disabled";

        if (device.PreviousKeyValidUntil is { } until &&
            DateTimeOffset.FromUnixTimeSeconds(until) > DateTimeOffset.UtcNow)
        {
            return "rotating";
        }

        return "ok";
    }

    private static string FormatAge(DateTimeOffset then)
    {
        var age = DateTimeOffset.UtcNow - then;

        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalMinutes < 60) return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes}m ago");
        if (age.TotalHours < 24) return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours}h ago");
        return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalDays}d ago");
    }

    private static string Trim(string value, int width)
        => value.Length <= width ? value : value[..(width - 1)] + "…";
}
