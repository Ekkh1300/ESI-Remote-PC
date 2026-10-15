using Esi.Core.Security;
using Esi.Protocol;
using Esi.Service;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// `DeviceStore.Has` is the agent's own test for a flag, written as `(Scopes &amp; scope) == scope`.
/// The enum has no such member, and `HasFlag` would answer true for `None` against anything,
/// so the same three characters are spelled out here instead of being approximated.
/// </summary>
internal static class ScopeFlagExtensions
{
    public static bool Has(this DeviceScope have, DeviceScope need) => (have & need) == need;
}

/// <summary>
/// The scope table, pinned against the routes the protocol actually publishes.
/// <para>
/// Nothing here existed before. `RequiredScope` is the single function that decides what a
/// paired phone is allowed to do, SECURITY.md prints its table, PROTOCOL.md prints a scope
/// column per route, and three documents could drift from each other for a release with no
/// test noticing - because the only place any of them was wrong would have been a 403 on a
/// handset nobody was holding.
/// </para>
/// <para>
/// This is also where the discrepancies found while auditing 1.0.1 were settled rather than
/// merely observed. PROTOCOL.md §6.10 marked <c>GET/PUT /notifications/rules</c> as
/// <c>admin</c>; the code and SECURITY.md §7 both say <c>read</c>, and the app ships a rules
/// editor every paired device can open. The code was right and the two rows of prose were
/// wrong, so the rows were corrected and the code's answer is what is pinned below.
/// </para>
/// <para>
/// Paths are matched by prefix and most-specific-first, which has one consequence worth
/// spelling out: <c>/api/v1/system/process</c> also matches <c>/api/v1/system/processes</c>.
/// A GET through that branch still lands on <c>read</c>, so the answer is right for the wrong
/// reason, and both spellings are listed below so a future split of the two cannot be silent.
/// </para>
/// </summary>
public sealed class ScopeTableTests
{
    private static DeviceScope Required(string path, string method) =>
        new RequestSignatureValidator().RequiredScope(path, method);

    // ---- The documented table ----------------------------------------------------------

    [Theory]
    // §6.1 identity - the four admin routes, and the one read route beside them.
    [InlineData("/api/v1/auth/whoami", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/auth/rotate", "POST", DeviceScope.Admin)]
    [InlineData("/api/v1/devices", "GET", DeviceScope.Admin)]
    [InlineData("/api/v1/devices/dev-1", "DELETE", DeviceScope.Admin)]
    [InlineData("/api/v1/devices/dev-1/rotate", "POST", DeviceScope.Admin)]
    // §6.11 settings.
    [InlineData("/api/v1/settings", "GET", DeviceScope.Admin)]
    [InlineData("/api/v1/settings", "PUT", DeviceScope.Admin)]
    [InlineData("/api/v1/settings/reset", "POST", DeviceScope.Admin)]
    // The two administrative reads.
    [InlineData("/api/v1/logs", "GET", DeviceScope.Admin)]
    [InlineData("/api/v1/audit", "GET", DeviceScope.Admin)]
    // §6.6 files - `files` for reads as well as writes, deliberately: enumerating a tree is
    // about as sensitive as writing to it.
    [InlineData("/api/v1/files/list", "GET", DeviceScope.Files)]
    [InlineData("/api/v1/files/roots", "GET", DeviceScope.Files)]
    [InlineData("/api/v1/files/download", "GET", DeviceScope.Files)]
    [InlineData("/api/v1/files/search", "GET", DeviceScope.Files)]
    [InlineData("/api/v1/files/upload-ticket", "POST", DeviceScope.Files)]
    [InlineData("/api/v1/files/upload", "PUT", DeviceScope.Files)]
    [InlineData("/api/v1/files/mkdir", "POST", DeviceScope.Files)]
    [InlineData("/api/v1/files/rename", "POST", DeviceScope.Files)]
    [InlineData("/api/v1/files/move", "POST", DeviceScope.Files)]
    [InlineData("/api/v1/files", "DELETE", DeviceScope.Files)]
    // §6.2 system - reads are reads whatever the route is called.
    [InlineData("/api/v1/system/info", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/system/metrics", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/system/storage", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/system/network", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/system/display", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/system/processes", "GET", DeviceScope.Read)]
    // ... except killing a process, which the service does with SeDebugPrivilege. Without
    // this branch the default below would hand process termination to a device holding only
    // `read`, and `read` would then mean SYSTEM.
    [InlineData("/api/v1/system/process/1234/terminate", "POST", DeviceScope.Control)]
    [InlineData("/api/v1/system/process/1234/terminate", "GET", DeviceScope.Read)]
    // §6.3 power, §6.5 apps, §6.7 media, §6.8 remote, §6.9 gaming - read for GET, control
    // for anything that changes the machine.
    [InlineData("/api/v1/power/status", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/power/wol/readiness", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/power/wake", "POST", DeviceScope.Control)]
    [InlineData("/api/v1/power/shutdown", "POST", DeviceScope.Control)]
    [InlineData("/api/v1/apps", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/apps/running", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/apps/launch", "POST", DeviceScope.Control)]
    [InlineData("/api/v1/apps/app-1/terminate", "POST", DeviceScope.Control)]
    [InlineData("/api/v1/apps/app-1/terminate", "DELETE", DeviceScope.Control)]
    [InlineData("/api/v1/media/state", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/media/playpause", "POST", DeviceScope.Control)]
    [InlineData("/api/v1/remote/session", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/remote/input", "POST", DeviceScope.Control)]
    [InlineData("/api/v1/remote/session", "DELETE", DeviceScope.Control)]
    [InlineData("/api/v1/gaming/state", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/gaming/fps", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/gaming/profiles", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/gaming/mode", "POST", DeviceScope.Control)]
    [InlineData("/api/v1/gaming/apps/app-1/state", "POST", DeviceScope.Control)]
    // §6.10 notifications - `read` for every verb, rules included. SECURITY.md §7 puts
    // `/notifications/rules` under "everything else", the app opens a rules editor on every
    // paired device, and only PROTOCOL.md ever claimed `admin` here.
    [InlineData("/api/v1/notifications", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/notifications", "DELETE", DeviceScope.Read)]
    [InlineData("/api/v1/notifications/42", "DELETE", DeviceScope.Read)]
    [InlineData("/api/v1/notifications/42/read", "POST", DeviceScope.Read)]
    [InlineData("/api/v1/notifications/read-all", "POST", DeviceScope.Read)]
    [InlineData("/api/v1/notifications/rules", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/notifications/rules", "PUT", DeviceScope.Read)]
    // Camera - the three routes PROTOCOL.md does not list at all. They fall into "everything
    // else" in SECURITY.md §7, which is `read` for every method, and they are read-only.
    [InlineData("/api/v1/camera/state", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/camera/latest", "GET", DeviceScope.Read)]
    [InlineData("/api/v1/camera/stream.mjpg", "GET", DeviceScope.Read)]
    // The WebSocket upgrade. It opens at `read` and each frame is checked again in the hub,
    // because everything multiplexed over the socket is more than a read.
    [InlineData("/ws", "GET", DeviceScope.Read)]
    public void The_scope_a_route_requires_is_the_documented_one(string path, string method, DeviceScope expected)
    {
        Assert.Equal(expected, Required(path, method));
    }

    // ---- The unauthenticated surface ---------------------------------------------------

    /// <summary>
    /// Everything a request with no signature at all can reach.
    /// <para>
    /// This list is short on purpose and it is the one table worth being paranoid about: every
    /// entry is reachable by anyone who can open a socket to the agent, with no pairing
    /// secret, no key and no nonce. The camera routes are deliberately absent - they need a
    /// signature, which is covered by the scope theory above rather than here.
    /// </para>
    /// <para>
    /// The negative half is a fixed list rather than a derivation, because the middleware
    /// answers an unknown path with 404 before it ever consults <see cref="SignatureAuthenticationMiddleware"/>
    /// and there is no way to enumerate registered endpoints from here. It pins every route
    /// the agent has today; a newly added route belongs in this data.
    /// </para>
    /// </summary>
    [Theory]
    // Public by design.
    [InlineData("/", true)]
    [InlineData("/api/v1/handshake", true)]
    [InlineData("/api/v1/health", true)]
    [InlineData("/api/v1/pair/request", true)]
    [InlineData("/api/v1/pair/session", true)]
    [InlineData("/pairing", true)]
    [InlineData("/pairing/qr.svg", true)]
    [InlineData("/assets/app.js", true)]
    // Everything else, and every one of these is a route the agent actually serves.
    [InlineData("/api/v1/auth/whoami", false)]
    [InlineData("/api/v1/auth/rotate", false)]
    [InlineData("/api/v1/devices", false)]
    [InlineData("/api/v1/settings", false)]
    [InlineData("/api/v1/settings/reset", false)]
    [InlineData("/api/v1/logs", false)]
    [InlineData("/api/v1/audit", false)]
    [InlineData("/api/v1/files/list", false)]
    [InlineData("/api/v1/files/roots", false)]
    [InlineData("/api/v1/files/download", false)]
    [InlineData("/api/v1/files/upload-ticket", false)]
    // The raw upload is authenticated by its single-use ticket rather than a signature, and
    // that is a different list with a different reason - it is not public, only unsigned.
    [InlineData("/api/v1/files/upload", false)]
    [InlineData("/api/v1/system/info", false)]
    [InlineData("/api/v1/system/metrics", false)]
    [InlineData("/api/v1/system/processes", false)]
    [InlineData("/api/v1/system/process/1234/terminate", false)]
    [InlineData("/api/v1/power/status", false)]
    [InlineData("/api/v1/power/wake", false)]
    [InlineData("/api/v1/apps", false)]
    [InlineData("/api/v1/apps/launch", false)]
    [InlineData("/api/v1/media/state", false)]
    [InlineData("/api/v1/media/playpause", false)]
    [InlineData("/api/v1/remote/session", false)]
    [InlineData("/api/v1/remote/input", false)]
    [InlineData("/api/v1/gaming/state", false)]
    [InlineData("/api/v1/gaming/mode", false)]
    [InlineData("/api/v1/notifications", false)]
    [InlineData("/api/v1/notifications/rules", false)]
    [InlineData("/api/v1/camera/state", false)]
    [InlineData("/api/v1/camera/latest", false)]
    // The MJPEG stream is listed here because its own doc comment says it is "deliberately
    // unauthenticated" so that OBS, VLC and ffmpeg can open it. `IsPublic` never returned true
    // for it, so it has always required a per-request signature those programs cannot produce
    // - the comment and the code disagree, and this pins the code, which is the safer of the
    // two to keep until someone decides which was meant.
    [InlineData("/api/v1/camera/stream.mjpg", false)]
    [InlineData("/ws", false)]
    public void The_unauthenticated_surface_is_exactly_this(string path, bool expected)
    {
        Assert.Equal(expected, SignatureAuthenticationMiddleware.IsPublic(path));
    }

    // ---- What a default pairing can actually reach -------------------------------------

    /// <summary>
    /// `pair approve` grants `read`, `control` and `files`, and nothing in the product grants
    /// `admin` - `DeviceStore.SetScopes` has no caller, and `DefaultScopes` is a const. So
    /// this is the reachable surface of every phone ever paired with this agent, and it is
    /// worth stating as a test rather than as a paragraph in the release notes, because the
    /// day someone adds a way to grant `admin` this fails and makes them decide on purpose.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/auth/whoami", "GET")]
    [InlineData("/api/v1/files/list", "GET")]
    [InlineData("/api/v1/files/upload-ticket", "POST")]
    [InlineData("/api/v1/system/info", "GET")]
    [InlineData("/api/v1/system/metrics", "GET")]
    [InlineData("/api/v1/system/processes", "GET")]
    [InlineData("/api/v1/system/process/1234/terminate", "POST")]
    [InlineData("/api/v1/power/status", "GET")]
    [InlineData("/api/v1/power/shutdown", "POST")]
    [InlineData("/api/v1/apps/running", "GET")]
    [InlineData("/api/v1/apps/launch", "POST")]
    [InlineData("/api/v1/media/state", "GET")]
    [InlineData("/api/v1/media/stop", "POST")]
    [InlineData("/api/v1/remote/input", "POST")]
    [InlineData("/api/v1/gaming/mode", "POST")]
    [InlineData("/api/v1/notifications/rules", "PUT")]
    [InlineData("/api/v1/camera/state", "GET")]
    [InlineData("/ws", "GET")]
    public void A_default_paired_device_reaches_this_route(string path, string method)
    {
        Assert.True(
            PairingManager.DefaultScopes.Has(Required(path, method)),
            $"a phone paired with DefaultScopes ({PairingManager.DefaultScopes}) needs "
            + $"{Required(path, method)} for {method} {path}");
    }

    /// <summary>
    /// The other half: the routes a default pairing is refused, with the reason written into
    /// the failure. Documented in `docs/RELEASE-NOTES-1.0.1.md` under "Not in this release",
    /// unchanged from 1.0.0 - this test is not asking for that to be fixed, it is making sure
    /// the list cannot change without someone noticing which features went with it.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/auth/rotate", "POST", "rotating this device's own keys")]
    [InlineData("/api/v1/devices", "GET", "the paired-device list")]
    [InlineData("/api/v1/devices/dev-1", "DELETE", "revoking a paired device")]
    [InlineData("/api/v1/settings", "PUT", "the agent's settings")]
    [InlineData("/api/v1/settings/reset", "POST", "resetting the agent's settings")]
    [InlineData("/api/v1/logs", "GET", "the agent's log tail")]
    [InlineData("/api/v1/audit", "GET", "the audit trail")]
    public void A_default_paired_device_is_refused_this_route(string path, string method, string feature)
    {
        Assert.False(
            PairingManager.DefaultScopes.Has(Required(path, method)),
            $"{feature} ({method} {path}) is now reachable on DefaultScopes, which would mean "
            + "the bootstrap deadlock described in docs/INSTALL.md is closed. Remove this case "
            + "and say so in the release notes.");
    }

    [Fact]
    /// <summary>
    /// The bootstrap deadlock, stated as three facts that are each testable.
    /// <para>
    /// `DefaultScopes` is what every pairing path passes, the admin prefix is what it is
    /// refused, and `DeviceScope.All` still contains `Admin` - so the flag exists and nothing
    /// in the product can hand it out. `docs/INSTALL.md` describes the escape hatch as
    /// `POST /api/v1/devices`, but the agent serves no such route: the only `/devices`
    /// endpoints are `GET`, `DELETE /{deviceId}` and `POST /{deviceId}/rotate`, and rotating
    /// keys does not change scopes. `DeviceStore.SetScopes`, the one method that would, has
    /// no caller.
    /// </para>
    /// </summary>
    public void Nothing_in_the_product_can_grant_admin()
    {
        Assert.False(PairingManager.DefaultScopes.Has(DeviceScope.Admin));
        Assert.Equal(DeviceScope.Admin, Required("/api/v1/devices", "POST"));
        Assert.True(DeviceScope.All.Has(DeviceScope.Admin),
            "the flag still exists, so the ceiling has not moved either");
    }
}
