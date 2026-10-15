using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Esi.Platform.Apps;
using Esi.Platform.Camera;
using Esi.Platform.Capture;
using Esi.Platform.Discovery;
using Esi.Platform.Files;
using Esi.Platform.Machine;
using Esi.Platform.Media;
using Esi.Platform.Power;
using Esi.Platform.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Esi.Service;

/// <summary>
/// The HTTP surface. Grouped by feature so each module's routes are readable together, and
/// kept free of business logic — every handler is a thin adapter over a service.
/// </summary>
public static class Endpoints
{
    private static RouteGroupBuilder Api(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGroup(ProtocolConstants.ApiBase);

    // =============================================================================
    // Public: handshake, health, pairing
    // =============================================================================

    public static void MapPublic(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/handshake", ([FromServices] HandshakeBuilder builder) => Results.Ok(builder.Build()));

        api.MapGet("/health", (HttpContext context, [FromServices] IdentityService identity, [FromServices] SessionAgentBridge session,
                [FromServices] AgentOptions options, [FromServices] CertificateAuthority authority) =>
        {
            var privileges = context.RequestServices.GetRequiredService<PrivilegeHelper>();

            return Results.Ok(new HealthResponse
            {
                Status = "ok",
                Version = options.Version,
                UptimeSeconds = SystemClock.UptimeSeconds(),
                ServerTime = AgentDatabase.NowUnix(),
                ServiceAccount = privileges.ServiceAccount,
                IsElevated = privileges.IsElevated,
                SessionId = privileges.SessionId,
                DebugMode = options.DebugMode,
            });
        });

        // ---- Pairing -----------------------------------------------------------
        api.MapPost("/pair/request", async (HttpContext context, PairRequest request,
            [FromServices] PairingManager pairing, [FromServices] RateLimiter limiter, [FromServices] AuditLog audit, [FromServices] HandshakeBuilder builder) =>
        {
            var ctx = RequestContextMiddleware.Get(context);

            // Pairing attempts are the one unauthenticated write endpoint, so they get
            // their own very tight per-address budget: 5 per hour.
            if (!limiter.TryConsumeForAddress(ctx.RemoteAddress, "pair"))
            {
                return Results.Json(
                    new Protocol.ErrorResponse { Error = new ErrorBody { Code = ErrorCodes.RateLimited,
                        Message = "Too many pairing attempts from this address. Try again later." } },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            try
            {
                var (response, _) = pairing.Create(request, ctx.RemoteAddressText);
                audit.Record(AuditLog.ActionPairRequested, "ok", remoteAddress: ctx.RemoteAddressText,
                    detail: $"{request.DeviceName} ({request.Platform}) code {response.UserCode}");

                return Results.Created($"{ProtocolConstants.ApiBase}/pair/session", response);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(
                    new Protocol.ErrorResponse { Error = new ErrorBody { Code = ErrorCodes.Conflict, Message = ex.Message } },
                    statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/pair/session", async (HttpContext context, string id, string? secret, string? code,
            [FromServices] PairingManager pairing, [FromServices] AuditLog audit) =>
        {
            var ctx = RequestContextMiddleware.Get(context);

            if (string.IsNullOrWhiteSpace(id))
                return BadRequest("The 'id' query parameter is required.");

            // The phone supplies the provisioning secret from the QR it scanned. Without it
            // the attempt can be polled for status, but the credentials are never released -
            // that is the entire point of the secret.
            byte[]? provisioningSecret = null;

            if (!string.IsNullOrWhiteSpace(secret))
            {
                // base64url, because the value travels in a query string and a '+' or a '/'
                // would have to be escaped. Standard base64 is accepted too, so a hand-built
                // client that copied the format from an older build still works.
                if (!TryDecodeBase64Url(secret, out provisioningSecret) &&
                    !TryDecodeBase64Url(secret.Replace('-', '+').Replace('_', '/'), out provisioningSecret))
                {
                    return BadRequest("The 'secret' parameter is not valid base64url.");
                }

                if (provisioningSecret!.Length != ProtocolConstants.SecretLength)
                {
                    return BadRequest(
                        $"The 'secret' parameter must decode to {ProtocolConstants.SecretLength} bytes.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(code))
            {
                var byCode = pairing.GetByUserCode(code);
                if (byCode is null)
                    return NotFound("No pairing attempt matches that code.");
            }

            var status = pairing.Poll(id, provisioningSecret);

            if (status.Status == "approved")
            {
                audit.Record(AuditLog.ActionPairApproved, "ok", status.DeviceId, remoteAddress: ctx.RemoteAddressText,
                    detail: $"secrets released for pairing {id}");
            }

            return Results.Ok(status);
        });

        // ---- Human-readable pairing page ---------------------------------------
        //
        // Two routes on purpose. The bare page is what a user opens first; the ?code= form
        // additionally shows the QR, and is what the app links to after it has a code to
        // show. The QR is how the provisioning secret reaches the phone when the session
        // agent is not there to show it on the desktop.
        endpoints.MapGet("/pairing", (HttpContext http, [FromServices] IdentityService identity,
                [FromServices] CertificateAuthority authority,
                [FromServices] PairingManager pairing,
                string? code) =>
        {
            var html = RenderPairingPage(identity, authority);

            // The address the user reached this page on, which is the one their phone can
            // resolve — not the agent's idea of its own LAN address, which is a different
            // interface on a multi-homed machine and produces a code that cannot connect.
            var seenHost = http.Request.Host.Host;

            // Opened from the PC itself, the host is "localhost", and a QR carrying that sends
            // the phone nowhere. Saying so - with the addresses that would work - is the
            // difference between a QR and a puzzle.
            if (IsLoopback(seenHost))
            {
                var reachable = string.Join(", ", LanAddresses());

                html = html.Replace(
                    "<!--LOOPBACK-->",
                    $"""
                     <div class="hint" style="border-color:rgba(251,191,36,.35);background:rgba(251,191,36,.10);color:#fde68a">
                       <b>This page is open on the PC itself</b>, so the QR below carries
                       <code>localhost</code> and a phone cannot use it. Open this page again from
                       the phone's side of the network &mdash; at
                       {(string.IsNullOrEmpty(reachable) ? "this machine's address on that network" : reachable)}
                       &mdash; and scan the QR there.
                     </div>
                     """,
                    StringComparison.Ordinal);
            }
            else
            {
                html = html.Replace("<!--LOOPBACK-->", string.Empty, StringComparison.Ordinal);
            }

            if (!string.IsNullOrWhiteSpace(code) && pairing.GetByUserCode(code) is { Status: PairingStatus.Pending })
            {
                // The image is sized from the code it points at, not from a fixed 240. A QR
                // scaled to a width that is not a whole number of pixels per module cannot be
                // read by a phone - which is what a real phone reported on this page (D37).
                var pending = pairing.GetByUserCode(code)!;
                var pollingUrl = pairing.BuildPollingUrl(pending, seenHost, pairing.Port);
                var extent = QrExtent(pollingUrl);

                html = html.Replace(
                    "<!--QR-->",
                    $"""
                     <section class="qr">
                       <p class="k">A device is asking to pair with code <b>{code}</b>.</p>
                       <p class="k">Scan this with the phone. It carries the one-time secret that
                          lets <em>that</em> phone - and only that phone - collect the credentials.</p>
                       <img src="/pairing/qr.svg?code={Uri.EscapeDataString(code)}" width="{extent}" height="{extent}"
                            alt="Pairing QR code for {code}">
                       <p class="k">Then scan this second code on the phone, and only then allow the
                          request on the PC. If you did not start this, choose Deny - approving a
                          pairing you did not initiate gives whoever asked for it control of this
                          machine.</p>
                     </section>
                     """,
                    StringComparison.Ordinal);
            }
            else
            {
                // Same reasoning as the pending case above: the image is sized from the code it
                // points at, so the browser never rescales a QR onto fractional pixels (D37).
                var connectExtent = QrExtent(
                    pairing.BuildConnectUrl(seenHost, identity.DeviceId, authority.SpkiPin));

                html = html.Replace(
                    "<!--QR-->",
                    $"""
                     <section class="qr">
                       <p class="k">Scan this with the phone to fill in the address, the device id
                          and the certificate pin &mdash; all three at once, nothing typed. It
                          carries no secret, so it is safe to leave on screen; the one-time secret
                          arrives on a second code once a phone is actually asking to pair.</p>
                       <img src="/pairing/qr.svg?connect=1&amp;host={Uri.EscapeDataString(seenHost)}"
                            width="{connectExtent}" height="{connectExtent}" alt="Pairing connect QR code">
                       <p class="k">Prefer to type it? The address is the one this page is open on,
                          the port is {pairing.Port}, and the pin is above.</p>
                     </section>
                    """,
                    StringComparison.Ordinal);
            }

            return Results.Content(html, "text/html; charset=utf-8");
        });

        // ---- The pairing QR ----------------------------------------------------
        //
        // This is the whole reason pairing can complete. The provisioning secret is never
        // sent to the requester over the network - that would hand it to the attacker the
        // secret exists to stop - so it has to travel from the PC to the phone by an act of
        // the user. A QR on the screen, scanned by the phone, is that act.
        //
        // Keyed by the six-digit user code rather than the internal attempt id, because the
        // user is the one who knows the code: they read it off their phone and type it here.
        // That also keeps the long-lived attempt id out of a URL that gets typed.
        endpoints.MapGet("/pairing/qr.svg", ([FromServices] PairingManager pairing,
                [FromServices] IdentityService identity,
                [FromServices] CertificateAuthority authority,
                string? code, string? host, string? connect) =>
        {
            // The "connect" QR: what a phone needs *before* it can ask to pair at all. No
            // attempt, therefore no secret - see PairingManager.BuildConnectUrl for why this
            // exists, and for why putting the pin into a QR on the PC's own screen discloses
            // nothing an attacker on the LAN did not already have.
            if (!string.IsNullOrEmpty(connect))
            {
                var connectUrl = pairing.BuildConnectUrl(
                    host ?? "localhost", identity.DeviceId, authority.SpkiPin);
                return Results.Content(
                    BuildQrSvg(Esi.Platform.Pairing.QrEncoder.Encode(connectUrl)),
                    "image/svg+xml; charset=utf-8");
            }

            var attempt = pairing.GetByUserCode(code ?? string.Empty);

            if (attempt is null || attempt.Status != PairingStatus.Pending)
            {
                return ApiError.Result(
                    StatusCodes.Status404NotFound,
                    ErrorCodes.NotFound,
                    "No pending pairing request matches that code. It may have expired, or been approved already.");
            }

            var url = pairing.BuildPollingUrl(attempt, host ?? "localhost", pairing.Port);
            return Results.Content(
                BuildQrSvg(Esi.Platform.Pairing.QrEncoder.Encode(url)),
                "image/svg+xml; charset=utf-8");
        });
    }

    /// <summary>
    /// The pixel width a QR will be drawn at, so the page can size its image to match.
    /// </summary>
    /// <remarks>
    /// The pairing page asks for this rather than pinning a width of its own, because a QR
    /// scaled to a width that is not a whole number of pixels per module is one a phone cannot
    /// read (D37). Six pixels per module, matching <see cref="QrModulePixels"/> here.
    /// </remarks>
    internal static int QrExtent(string payload)
        => (Esi.Platform.Pairing.QrEncoder.Encode(payload).GetLength(0) + 8) * QrModulePixels;

    /// <summary>Pixels per QR module, shared by the renderer and the page's sizing.</summary>
    internal const int QrModulePixels = 6;

    /// <summary>
    /// Renders a QR code as an SVG.
    /// <para>
    /// SVG rather than a PNG so the code stays sharp on a high-DPI display from across a
    /// desk, and so it needs no imaging library in the service - which matters because the
    /// service is the process least able to take a dependency on a desktop graphics stack.
    /// </para>
    /// </summary>
    /// <summary>
    /// Decodes base64url, tolerating the padding a URL may have dropped.
    /// </summary>
    private static bool TryDecodeBase64Url(string value, out byte[]? bytes)
    {
        bytes = null;

        var padded = value.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;

        if (remainder == 1)
            return false; // Never a valid base64 length.

        if (remainder != 0)
            padded = padded.PadRight(padded.Length + (4 - remainder), '=');

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string RenderQrSvg(string payload)
    {
        var modules = Esi.Platform.Pairing.QrEncoder.Encode(payload);
        return BuildQrSvg(modules);
    }

    /// <summary>
    /// Builds the SVG for an encoded QR.
    /// <para>
    /// The extent is a whole number of pixels per module, and the caller is told what it is, so
    /// the browser can draw the code at 1:1 instead of scaling it. That is not a cosmetic point
    /// (D37). This page used to pin <c>width="240"</c> on the image, and a polling URL needs 53
    /// cells across - so the browser fitted 53 cells into 240 pixels, or 4.528 each. Half the
    /// module boundaries landed between pixels, the grid stopped being square, and a phone's
    /// scanner reported no code at all. Asking for 318 when 318 was what was already drawn is
    /// the whole fix.
    /// </para>
    /// </summary>
    private static string BuildQrSvg(bool[,] modules)
    {
        var size = modules.GetLength(0);

        // The quiet zone is part of the specification, not decoration: a scanner needs four
        // clear modules around the symbol to establish timing before it starts reading.
        const int quiet = 4;
        var moduleSize = QrModulePixels;
        var extent = (size + quiet * 2) * moduleSize;

        var path = new StringBuilder(extent * extent / 8);

        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                if (!modules[row, col])
                    continue;

                var x = (col + quiet) * moduleSize;
                var y = (row + quiet) * moduleSize;

                path.Append(CultureInfo.InvariantCulture, $"M{x} {y}h{moduleSize}v{moduleSize}h-{moduleSize}z");
            }
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <svg xmlns="http://www.w3.org/2000/svg" width="{extent}" height="{extent}"
                 viewBox="0 0 {extent} {extent}" shape-rendering="crispEdges" role="img"
                 aria-label="Pairing code">
              <rect width="{extent}" height="{extent}" fill="#ffffff"/>
              <path d="{path}" fill="#000000"/>
            </svg>
            """;
    }

    /// <summary>True when the host a request arrived on can only ever mean this machine.</summary>
    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host is "::1" or "[::1]" ||
        host.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>
    /// This machine's own IPv4 addresses, so the pairing page can name them when it has been
    /// opened on localhost. Operational, routable ones only: Windows also lists loopback,
    /// link-local and APIPA addresses, and none of those is how a phone reaches a PC.
    /// </summary>
    private static IEnumerable<string> LanAddresses()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    continue;

                var text = unicast.Address.ToString();
                if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                found.Add(text);
            }
        }

        return found;
    }

    private static string RenderPairingPage([FromServices] IdentityService identity, [FromServices] CertificateAuthority authority)
    {
        var pin = authority.SpkiPin ?? "(TLS unavailable)";
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <title>ESI Remote PC &mdash; {{identity.DeviceName}}</title>
            <style>
              :root { color-scheme: dark; }
              body { margin:0; min-height:100vh; display:grid; place-items:center;
                     background:radial-gradient(1200px 600px at 30% -10%, #1b2a4a 0%, #0a0e17 60%);
                     font:16px/1.6 system-ui,-apple-system,"Segoe UI",sans-serif; color:#e8edf7; }
              .card { background:rgba(255,255,255,.05); border:1px solid rgba(255,255,255,.10);
                      backdrop-filter:blur(18px); border-radius:20px; padding:36px 40px;
                      max-width:640px; box-shadow:0 24px 70px rgba(0,0,0,.45); }
              h1 { margin:0 0 4px; font-size:22px; letter-spacing:.2px; }
              .sub { color:#93a4c4; margin:0 0 26px; }
              .row { display:flex; justify-content:space-between; gap:16px; padding:11px 0;
                     border-bottom:1px solid rgba(255,255,255,.07); }
              .row:last-of-type { border-bottom:0; }
              .k { color:#93a4c4; }
              .v { font-family:ui-monospace,SFMono-Regular,Menlo,monospace; font-size:13px;
                    word-break:break-all; text-align:right; }
              .pin { color:#5eead4; }
              .hint { margin-top:24px; padding:14px 16px; border-radius:12px; font-size:13px;
                      background:rgba(94,234,212,.08); border:1px solid rgba(94,234,212,.22); color:#b8f0e6; }
              code { background:rgba(255,255,255,.08); padding:2px 7px; border-radius:6px; font-size:13px; }
              .qr { margin:24px 0; padding:20px; border-radius:14px; text-align:center;
                    background:rgba(94,214,255,.06); border:1px solid rgba(94,214,255,.20); }
              .qr .k { display:block; margin:0 0 10px; }
              .qr img { border-radius:8px; background:#fff; padding:6px; margin:12px 0; }
              .qr b { color:#5eead4; letter-spacing:2px; font-size:18px; }
            </style></head>
            <body><div class="card">
              <h1>ESI Remote PC</h1>
              <p class="sub">This PC is running the ESI Remote PC agent.</p>
              <div class="row"><span class="k">Device</span><span class="v">{{identity.DeviceName}}</span></div>
              <div class="row"><span class="k">Device ID</span><span class="v">{{identity.DeviceId}}</span></div>
              <div class="row"><span class="k">TLS SPKI pin</span><span class="v pin">{{pin}}</span></div>
              <div class="row"><span class="k">API</span><span class="v">{{ProtocolConstants.ApiBase}}</span></div>
              <!--LOOPBACK-->
              <!--QR-->
              <div class="hint">
                <b>How pairing works.</b> The phone asks to pair and shows you a six-digit code.
                Open this page with <code>?code=123456</code> and the phone's code is turned into
                a QR that the phone scans. That QR carries a one-time secret, and only a phone
                holding it can collect the credentials &mdash; so a device that merely triggered
                a prompt cannot steal them. Approve the request only if you started it.
                <br><br>
                You can also approve from a console with
                <code>esirc pair approve &lt;code&gt;</code>.
              </div>
            </div></body></html>
            """;
    }

    // =============================================================================
    // System, metrics, identity
    // =============================================================================

    public static void MapSystem(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/auth/whoami", (HttpContext context, [FromServices] AgentOptions options) =>
        {
            var ctx = RequestContextMiddleware.Get(context);
            return Results.Ok(new WhoAmIResponse
            {
                DeviceId = ctx.Device!.DeviceId,
                KeyId = ctx.KeyId,
                Scopes = ScopeNames(ctx.Device.Scopes),
                Name = ctx.Device.Name,
                Platform = ctx.Device.Platform,
                FirstPaired = ctx.Device.FirstPaired,
                LastSeen = ctx.Device.LastSeen,
                LastAddress = ctx.Device.LastAddress,
                RotatedAt = ctx.Device.RotatedAt,
                AgentVersion = options.Version,
            });
        });

        api.MapGet("/system/info", async (HttpContext context, string? detail, [FromServices] SystemInfoProvider info,
            [FromServices] MetricsService metrics, [FromServices] SessionAgentBridge session, [FromServices] AgentOptions options,
            [FromServices] CertificateAuthority authority, [FromServices] GpuTelemetrySummary gpuSummary) =>
        {
            var ctx = RequestContextMiddleware.Get(context);
            var summaryOnly = detail?.Equals("summary", StringComparison.OrdinalIgnoreCase) == true;
            var metricsSnapshot = await metrics.GetAsync(500).ConfigureAwait(false);

            var response = new SystemInfoResponse
            {
                HostName = Environment.MachineName,
                DeviceName = ctx.Device!.DeviceId.Length > 0 ? options.Settings.DeviceName ?? Environment.MachineName : Environment.MachineName,
                Os = info.GetOsInfo(),
                Cpu = info.GetCpuInfo(),
                Memory = info.GetMemoryInfo(),
                Storage = info.GetStorageVolumes(),
                Bios = info.GetBiosInfo(),
                Security = info.GetSecurityInfo(),
                Power = info.GetPowerCapabilities(),
                Sensors = gpuSummary.Summarise(metricsSnapshot),
                Agent = new AgentInfo
                {
                    Version = options.Version,
                    ProtocolVersion = ProtocolConstants.Version,
                    UptimeSeconds = SystemClock.UptimeSeconds(),
                    DebugMode = options.DebugMode,
                    SessionAgentConnected = session.IsConnected,
                    SessionAgentState = session.State.ToString(),
                },
            };

            var (manufacturer, model) = info.GetComputerIdentity();
            response.Manufacturer = manufacturer;
            response.Model = model;
            response.Security.LanOnly = true;
            response.Gpu = summaryOnly ? [] : info.GetGpuInfo();

            if (!summaryOnly)
            {
                response.Cpu = info.GetCpuInfo();
            }

            return Results.Ok(response);
        });

        api.MapGet("/system/metrics", async (HttpContext context, int? window, [FromServices] MetricsService metrics,
            CancellationToken ct) =>
        {
            var sampleWindow = Math.Clamp(window ?? 1000, 250, 10000);
            return Results.Ok(await metrics.GetAsync(sampleWindow, ct).ConfigureAwait(false));
        });

        api.MapGet("/system/network", ([FromServices] NetworkProvider network) =>
        {
            var interfaces = network.GetInterfaces();
            var primary = network.GetPrimaryInterface();

            foreach (var nic in interfaces)
                nic.IsPrimary = primary is not null && nic.Id == primary.Id;

            return Results.Ok(new NetworkMetrics
            {
                Interfaces = interfaces,
                Primary = primary,
            });
        });

        api.MapGet("/system/display", ([FromServices] GdiScreenCaptureProvider capture) =>
            Results.Ok(capture.EnumerateDisplays().Select(d => new DisplayInfo
            {
                Id = d.Id,
                Name = d.Name,
                X = d.X,
                Y = d.Y,
                Width = d.Width,
                Height = d.Height,
                IsPrimary = d.IsPrimary,
                Rotation = d.Rotation,
                RefreshRateHz = d.RefreshRateHz,
                ScalePercent = d.ScalePercent,
            })));

        api.MapGet("/system/processes", ([FromServices] AppLauncher launcher, string? sort, int? limit) =>
        {
            var processes = launcher.ListRunning()
                .Select(p => new ProcessInfoDto
                {
                    Pid = p.Pid,
                    Name = p.Name,
                    WorkingSetBytes = p.WorkingSet,
                    StartedAt = UnixTime.Seconds(p.StartedAt),
                })
                .ToList();

            processes = (sort?.ToLowerInvariant()) switch
            {
                "memory" => processes.OrderByDescending(p => p.WorkingSetBytes).ToList(),
                "name" => processes.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                _ => processes.OrderByDescending(p => p.WorkingSetBytes).ToList(),
            };

            return Results.Ok(new ProcessListResponse
            {
                Processes = processes.Take(Math.Clamp(limit ?? 50, 1, 200)).ToList(),
            });
        });

        api.MapPost("/system/process/{pid:int}/terminate", async (int pid, [FromServices] PowerService power) =>
        {
            // Termination is routed through the same privilege helper the power service
            // uses, because killing a process owned by another user needs SeDebugPrivilege.
            var privileges = new PrivilegeHelper(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PrivilegeHelper>.Instance);
            privileges.Enable(PrivilegeHelperNames.Debug);

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                await Task.CompletedTask;
                return Results.Ok(new { terminated = true, pid });
            }
            catch (ArgumentException)
            {
                return Results.Json(
                    new Protocol.ErrorResponse { Error = new ErrorBody { Code = ErrorCodes.NotFound, Message = $"No process with id {pid}." } },
                    statusCode: StatusCodes.Status404NotFound);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new Protocol.ErrorResponse { Error = new ErrorBody { Code = ErrorCodes.Internal, Message = ex.Message } },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    // =============================================================================
    // Power
    // =============================================================================

    public static void MapPower(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/power/status", ([FromServices] PowerService power) => Results.Ok(power.GetStatus()));

        api.MapPost("/power/{action}", async (HttpContext context, string action, PowerActionRequest? request,
            [FromServices] PowerService power, [FromServices] AuditLog audit, [FromServices] NotificationEngine notifications) =>
        {
            var ctx = RequestContextMiddleware.Get(context);
            var body = request ?? new PowerActionRequest();
            var normalized = action.ToLowerInvariant();

            var outcome = normalized switch
            {
                "shutdown" => power.Shutdown(restart: false, body.Force, body.DelaySeconds, body.Message),
                "restart" or "forcerestart" => power.Shutdown(restart: true, body.Force || normalized == "forcerestart", body.DelaySeconds, body.Message),
                "sleep" => power.Suspend(hibernate: false, body.Force),
                "hibernate" => power.Suspend(hibernate: true, body.Force),
                "lock" => power.Lock(),
                "signout" or "logoff" => power.SignOut(body.Force),
                _ => (PowerOutcome?)null,
            };

            if (outcome is null)
            {
                return Results.Json(
                    new Protocol.ErrorResponse { Error = new ErrorBody { Code = ErrorCodes.NotFound,
                        Message = $"Unknown power action '{action}'." } },
                    statusCode: StatusCodes.Status404NotFound);
            }

            audit.Record(AuditLog.ActionPowerAction, outcome.Accepted ? "ok" : "failed",
                ctx.Device!.DeviceId, ctx.Device.Name, ctx.RemoteAddressText,
                $"{normalized}: {outcome.Detail}");

            if (outcome.Accepted)
            {
                notifications.RaiseEvent("power", $"PC is {normalized}",
                    outcome.Detail ?? $"Requested from {ctx.Device.Name}.");
            }

            return outcome.Accepted
                ? Results.Ok(new PowerActionResponse
                {
                    Accepted = true,
                    Action = normalized,
                    EffectiveMode = outcome.EffectiveMode,
                    ScheduledAt = outcome.ScheduledAt,
                    Warning = outcome.Warning,
                    RequiresInteractiveSession = outcome.RequiresInteractiveSession,
                    Detail = outcome.Detail,
                })
                : Results.Json(new PowerActionResponse
                {
                    Accepted = false,
                    Action = normalized,
                    Warning = outcome.Warning,
                    RequiresInteractiveSession = outcome.RequiresInteractiveSession,
                    Detail = outcome.Detail,
                }, statusCode: StatusCodes.Status409Conflict);
        });

        api.MapGet("/power/wol/readiness", ([FromServices] NetworkProvider network, [FromServices] PowerService power) =>
        {
            var armed = PowerStateProbe.GetWakeArmedAdapters()
                .Select(a => a.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var devices = network.GetWakeCapableAdapters()
                .Select(adapter =>
                {
                    var device = new WolDevice
                    {
                        Name = adapter.Name,
                        MacAddress = adapter.Mac,
                        ConnectionType = adapter.Type,
                    };

                    // powercfg's names do not always match the friendly adapter name, so a
                    // partial match is used; an unmatched adapter is reported as not armed
                    // rather than optimistically assumed armed.
                    device.WakeMagicPacket = armed.Any(a =>
                        a.Contains(adapter.Name, StringComparison.OrdinalIgnoreCase) ||
                        adapter.Name.Contains(a, StringComparison.OrdinalIgnoreCase));

                    return device;
                })
                .ToList();

            var hiberboot = PowerStateProbe.IsHiberbootEnabled();
            var issues = new List<string>();

            if (devices.Count == 0)
                issues.Add("No adapter with a hardware address was found, so Wake-on-LAN cannot be sent.");

            if (!devices.Any(d => d.WakeMagicPacket))
                issues.Add("No adapter reports Wake on Magic Packet as armed. Enable it in the adapter's advanced properties, or run: powercfg /deviceenablewake \"<adapter name>\"");

            if (!devices.Any(d => d.ConnectionType == "ethernet"))
                issues.Add("Wake-on-LAN only works over a wired Ethernet connection in practice. A Wi-Fi adapter cannot wake the machine.");

            if (hiberboot)
                issues.Add("Fast Startup is enabled, so a Windows restart does not fully power-cycle the NIC and the PC will not wake from a restart. Disable it with: powercfg /h off");

            return Results.Ok(new WolReadinessResponse
            {
                Ready = issues.Count == 0,
                ArmedDevices = devices,
                MacAddresses = [.. network.GetMacAddresses()],
                BroadcastAddresses = [.. network.GetRecommendedBroadcastAddresses()],
                HiberbootEnabled = hiberboot,
                Issues = issues,
            });
        });

        api.MapPost("/power/wake", async (HttpContext context, WakeRequest request,
            [FromServices] WakeOnLanSender wol, [FromServices] NetworkProvider network, [FromServices] AuditLog audit) =>
        {
            if (!WakeOnLanSender.TryParseMac(request.MacAddress, out var mac))
            {
                return BadRequest("The MAC address must be six hex bytes, for example AA:BB:CC:DD:EE:FF.");
            }

            var broadcasts = new List<IPAddress>();
            foreach (var value in request.BroadcastAddresses ?? [])
            {
                if (IPAddress.TryParse(value, out var address))
                    broadcasts.Add(address);
            }

            if (broadcasts.Count == 0)
                broadcasts.AddRange(network.GetRecommendedBroadcastAddresses().Select(IPAddress.Parse));

            var (sent, targets, ports) = await wol.SendAsync(mac, broadcasts, request.Ports, request.Repeat ?? 3,
                context.RequestAborted).ConfigureAwait(false);

            audit.Record(AuditLog.ActionPowerAction, "ok",
                RequestContextMiddleware.Get(context).Device?.DeviceId,
                detail: $"wake {WakeOnLanSender.FormatMac(mac)} -> {string.Join(", ", targets)}");

            return Results.Ok(new WakeResponse { Sent = sent, Targets = targets, Ports = ports });
        });
    }

    // =============================================================================
    // Apps
    // =============================================================================

    public static void MapApps(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/apps", ([FromServices] AppService apps) => Results.Ok(apps.List()));
        api.MapGet("/apps/running", ([FromServices] AppService apps) => Results.Ok(apps.Running()));

        api.MapPost("/apps/launch", async (HttpContext context, LaunchAppRequest request, [FromServices] AppService apps,
            [FromServices] AuditLog audit) =>
        {
            var ctx = RequestContextMiddleware.Get(context);
            var result = apps.Launch(request, ctx.Device!.DeviceId);

            // Every launch is recorded, whatever the outcome. The 409 carries a reason the phone
            // shows, but nothing was kept anywhere, so a launch that quietly did nothing left no
            // trace at all - and the next question was always "did it even arrive?" (D42).
            audit.Record(AuditLog.ActionAppLaunched, result.Launched ? "ok" : "refused",
                deviceId: ctx.Device!.DeviceId, deviceName: ctx.Device.Name,
                remoteAddress: ctx.RemoteAddressText,
                detail: $"{request.Id} -> {(result.Launched ? $"pid {result.ProcessId}" : result.Detail)}");

            return result.Launched
            ? Results.Ok(result)
            : Results.Json(result, statusCode: StatusCodes.Status409Conflict);
        });

        api.MapPost("/apps/{id}/terminate", (string id, [FromServices] AppService apps) => Results.Ok(apps.Terminate(id, force: false)));
        api.MapDelete("/apps/{id}/terminate", (string id, [FromServices] AppService apps) => Results.Ok(apps.Terminate(id, force: true)));
    }

    // =============================================================================
    // Files
    // =============================================================================

    public static void MapFiles(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/files/roots", ([FromServices] FileService files) => Results.Ok(files.GetRoots()));

        api.MapGet("/files/list", ([FromServices] FileService files, string? path, bool? showHidden) =>
            Guard(() => files.List(path, showHidden ?? false)));

        api.MapGet("/files/stat", ([FromServices] FileService files, string path) =>
            Guard(() => files.Stat(path)));

        api.MapGet("/files/search", ([FromServices] FileService files, string? root, string? query, int? maxResults) =>
            Guard(() => files.Search(root, query ?? string.Empty, maxResults ?? 200)));

        api.MapGet("/files/recent", ([FromServices] FileService files, int? limit) =>
            Guard(() => files.Recent(limit ?? 50)));

        api.MapPost("/files/mkdir", async ([FromServices] FileService files, FileOperationRequest request, HttpContext context) =>
        {
            var result = Guard(() => files.CreateDirectory(request.Path ?? string.Empty));
            return result;
        });

        api.MapPost("/files/rename", async ([FromServices] FileService files, FileOperationRequest request) =>
            Guard(() => files.Rename(request.From ?? string.Empty, request.To ?? string.Empty)));

        api.MapPost("/files/move", async ([FromServices] FileService files, FileOperationRequest request) =>
            Guard(() => files.Move(request.From ?? string.Empty, request.To ?? string.Empty)));

        api.MapDelete("/files", async ([FromServices] FileService files, string path, HttpContext context) =>
            Guard(() => files.Delete(path, RequestContextMiddleware.Get(context).Device?.DeviceId)));

        api.MapGet("/files/download", async (HttpContext context, [FromServices] FileService files, string path) =>
        {
            System.IO.Stream stream;
            long length;
            string name;

            try
            {
                (stream, length, name) = files.OpenDownload(path);
            }
            catch (Exception ex) when (ex is SandboxViolationException or FileAccessDeniedException
                                           or UnauthorizedAccessException or IOException)
            {
                return ApiError.FromFileException(ex);
            }

            await using (stream)
            {
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = length;
                context.Response.Headers.ContentDisposition =
                    $"attachment; filename=\"{Uri.EscapeDataString(name)}\"";

                await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }

            return Results.Empty;
        });

        api.MapPost("/files/upload-ticket", async (HttpContext context, UploadTicketRequest request,
            [FromServices] UploadTicketStore tickets, [FromServices] SettingsStore settings) =>
        {
            var ctx = RequestContextMiddleware.Get(context);

            if (string.IsNullOrWhiteSpace(request.Path))
                return BadRequest("A 'path' is required.");

            var response = tickets.Issue(ctx.Device!.DeviceId, request.Path, request.Size,
                settings.Current.Files.MaxUploadBytes);

            return Results.Created(response.UploadUrl, response);
        });

        // The raw upload. Authenticated by a single-use ticket rather than a body
        // signature, because signing an 8 MiB+ body would mean hashing it on every hop.
        api.MapMethods("/files/upload", ["PUT", "POST"], async (HttpContext context, string? ticket,
            [FromServices] UploadTicketStore tickets, [FromServices] FileService files) =>
        {
            if (string.IsNullOrWhiteSpace(ticket))
            {
                await ApiError.WriteAsync(context, StatusCodes.Status401Unauthorized,
                    ErrorCodes.Unauthorized, "An upload ticket is required.").ConfigureAwait(false);
                return Results.Empty;
            }

            var (valid, deviceId, path, maxBytes, error) = tickets.TryConsume(ticket);
            if (!valid)
            {
                await ApiError.WriteAsync(context, StatusCodes.Status401Unauthorized,
                    ErrorCodes.Unauthorized, error ?? "The upload ticket is not valid.").ConfigureAwait(false);
                return Results.Empty;
            }

            try
            {
                var declared = context.Request.ContentLength ?? 0;
                var result = files.WriteUpload(path!, context.Request.Body, declared, deviceId);
                return Results.Ok(result);
            }
            catch (Exception ex) when (ex is SandboxViolationException or FileAccessDeniedException
                                           or UnauthorizedAccessException or IOException)
            {
                return ApiError.FromFileException(ex);
            }
        });
    }

    // =============================================================================
    // Media
    // =============================================================================

    public static void MapMedia(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/media/state", async (HttpContext context, MediaController media,
            ISessionAgentBridge session) =>
            Results.Ok(await MediaStateAsync(session, media, context.RequestAborted).ConfigureAwait(false)));

        api.MapPost("/media/{action}", async (HttpContext context, string action, MediaActionRequest? request,
            [FromServices] MediaController media, ISessionAgentBridge session) =>
        {
            var body = request ?? new MediaActionRequest();
            var normalized = action.ToLowerInvariant();

            if (!Enum.TryParse<MediaAction>(action, ignoreCase: true, out var mediaAction))
            {
                return Results.Json(
                    new Protocol.ErrorResponse { Error = new ErrorBody { Code = ErrorCodes.NotFound,
                        Message = $"Unknown media action '{action}'." } },
                    statusCode: StatusCodes.Status404NotFound);
            }

            // Transport keys are session-bound: injecting them from the service in
            // session 0 would go nowhere, so they are forwarded to the session agent.
            //
            // The comment above described the intent, but the body did not carry it out - it
            // called media.SendTransport on the service's own controller, which is the session-0
            // injector. From there SendInput succeeds and reaches nothing, so the phone got 200
            // and play and pause quietly did nothing (D43).
            if (MediaController.TransportVirtualKey(mediaAction) is not null)
            {
                if (!session.IsConnected)
                {
                    return Results.Json(new MediaActionResponse
                    {
                        Accepted = false,
                        Action = normalized,
                        Detail = "Media keys must be sent from the interactive desktop. " +
                                 "Log in to the PC, or start 'ESI Remote PC Session'.",
                    }, statusCode: StatusCodes.Status409Conflict);
                }

                var ctx = RequestContextMiddleware.Get(context);
                if (!session.SendMediaTransport(mediaAction.ToString(), ctx.Device?.DeviceId ?? "unknown"))
                {
                    return Results.Json(new MediaActionResponse
                    {
                        Accepted = false,
                        Action = normalized,
                        Detail = "The session agent did not accept the media command.",
                    }, statusCode: StatusCodes.Status409Conflict);
                }

                var afterTransport = await MediaStateAsync(session, media, context.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Ok(new MediaActionResponse
                {
                    Accepted = true,
                    Action = normalized,
                    Volume = afterTransport.Volume,
                    IsMuted = afterTransport.IsMuted,
                });
            }

            // Volume and mute keys are session-bound too - they are injected with SendInput, and the
            // service sits in session 0 with no interactive desktop and no Core Audio endpoint.
            // They were missing from this check, so they were pressed from session 0, went
            // nowhere, and came back as a bare 409 that the phone could not explain (D43).
            //
            // volumeSet is handed over as an absolute target rather than as a run of presses,
            // because turning "move to 30" into presses needs the current volume - and reading
            // that is exactly what session 0 cannot do. The session agent owns the walk.
            if (IsVolumeControl(mediaAction))
            {
                if (!session.IsConnected)
                {
                    return Results.Json(new MediaActionResponse
                    {
                        Accepted = false,
                        Action = normalized,
                        Detail = "Volume keys must be pressed from the interactive desktop. " +
                                 "Log in to the PC, or start 'ESI Remote PC Session'.",
                    }, statusCode: StatusCodes.Status409Conflict);
                }

                var ctx = RequestContextMiddleware.Get(context);
                var sent = mediaAction == MediaAction.VolumeSet
                    ? session.SetMediaVolume(Math.Clamp(body.Volume ?? 50, 0, 100), normalized,
                        ctx.Device?.DeviceId ?? "unknown")
                    : session.SendMediaKey(
                        MediaController.VolumeVirtualKey(mediaAction)!.Value,
                        mediaAction is MediaAction.Mute or MediaAction.Unmute or MediaAction.ToggleMute
                            ? 1
                            : Math.Clamp(body.Step ?? 5, 1, 25),
                        normalized, ctx.Device?.DeviceId ?? "unknown");

                if (!sent)
                {
                    return Results.Json(new MediaActionResponse
                    {
                        Accepted = false,
                        Action = normalized,
                        Detail = "The session agent did not accept the volume command.",
                    }, statusCode: StatusCodes.Status409Conflict);
                }

                // Read after the keys land, so the phone shows where the PC actually is rather
                // than the number it asked for.
                //
                // Routed through the session agent, same as the write. The service is in
                // session 0 with no audio endpoint, so its own read here was always 0 - which
                // is why the reply said "volume is 0" immediately after accepting an instruction
                // to move it to 60.
                //
                // No race with the walk above: this is a named pipe, so the query is written
                // after the volume command and the agent processes them in that order.
                var after = await MediaStateAsync(session, media, context.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Ok(new MediaActionResponse
                {
                    Accepted = true,
                    Action = normalized,
                    Volume = after.VolumeKnown ? after.Volume : null,
                    IsMuted = after.IsMuteKnown ? after.IsMuted : null,
                });
            }

            // Both guards above have now claimed every action the enum defines - the transport ones and the
            // volume ones - so reaching here means the request named an action that exists but
            // routes nowhere. Injecting it here would be the session-0 mistake again, so it is
            // refused with a reason instead. A test asserts this switch never grows an arm that
            // calls into MediaController on the service's own behalf.
            return Results.Json(new MediaActionResponse
            {
                Accepted = false,
                Action = normalized,
                Detail = $"'{normalized}' has no route to the interactive desktop.",
            }, statusCode: StatusCodes.Status409Conflict);
        });
    }

    /// <summary>
    /// The PC's media state as the phone should see it.
    /// <para>
    /// Always asked of the session agent when one is connected. The service runs in session 0,
    /// which gets no audio endpoint at all, so every read it made for itself came back unknown -
    /// and unknown used to be reported as a confident 0, so a PC at 100% read as silent next to a
    /// slider the user had just moved (D43).
    /// </para>
    /// <para>
    /// A 750 ms guard, not a budget: this is a local named-pipe round trip, and the timeout only
    /// exists so a wedged agent cannot hold a request open. On timeout the service's own read is
    /// used, which carries <c>volumeKnown: false</c> rather than a fabricated level.
    /// </para>
    /// </summary>
    private static async Task<MediaStateResponse> MediaStateAsync(
        ISessionAgentBridge session,
        MediaController media,
        CancellationToken cancellationToken)
    {
        var fallback = media.GetState();

        if (!session.IsConnected)
            return fallback;

        var fromAgent = await session
            .QueryMediaStateAsync(TimeSpan.FromMilliseconds(750), cancellationToken)
            .ConfigureAwait(false);

        if (fromAgent is null)
            return fallback;

        return new MediaStateResponse
        {
            Volume = fromAgent.Volume,
            VolumeKnown = fromAgent.VolumeKnown,
            IsMuted = fromAgent.IsMuted,
            IsMuteKnown = fromAgent.IsMuteKnown,
            DefaultDevice = fromAgent.DefaultDevice ?? fallback.DefaultDevice,
            SessionPeak = fallback.SessionPeak,
            Supported = true,
            AvailableActions = fallback.AvailableActions,
        };
    }

    /// <summary>
    /// True for the actions that end up pressing a volume or mute key, which therefore have to be
    /// injected by the session agent rather than the service's own session 0 (D43).
    /// </summary>
    private static bool IsVolumeControl(MediaAction action) => action
        is MediaAction.VolumeUp or MediaAction.VolumeDown or MediaAction.VolumeSet
        or MediaAction.Mute or MediaAction.Unmute or MediaAction.ToggleMute;

    // =============================================================================
    // Notifications
    // =============================================================================

    public static void MapNotifications(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/notifications", ([FromServices] NotificationStore store, bool? unreadOnly, int? limit, long? since) =>
            Results.Ok(store.List(unreadOnly ?? false, limit ?? 100, since)));

        api.MapPost("/notifications/{id:long}/read", (long id, [FromServices] NotificationStore store) =>
            Results.Ok(new { read = store.MarkRead(id) }));

        api.MapPost("/notifications/read-all", ([FromServices] NotificationStore store) =>
            Results.Ok(new { updated = store.MarkAllRead() }));

        api.MapDelete("/notifications/{id:long}", (long id, [FromServices] NotificationStore store) =>
            Results.Ok(new { deleted = store.Delete(id) }));

        api.MapDelete("/notifications", ([FromServices] NotificationStore store) =>
            Results.Ok(new { deleted = store.DeleteAll() }));

        api.MapGet("/notifications/rules", ([FromServices] SettingsStore settings) =>
            Results.Ok(new NotificationRulesResponse { Rules = settings.Current.Notifications.Rules }));

        api.MapPut("/notifications/rules", async (NotificationRulesResponse body, [FromServices] SettingsStore settings,
            [FromServices] NotificationEngine engine, [FromServices] WebSocketHub hub, [FromServices] AuditLog audit, HttpContext context) =>
        {
            var incoming = body.Rules ?? [];
            settings.Current.Notifications.Rules = NotificationRuleCatalog.Merge(incoming);
            await settings.SaveAsync(settings.Current).ConfigureAwait(false);

            // Rule edits reset the evaluation state so thresholds take effect at once
            // instead of waiting for the old sustain timers to expire.
            engine.ResetState();
            engine.Prime(settings.Current.Notifications.Rules);

            audit.Record(AuditLog.ActionSettingsChanged, "ok",
                RequestContextMiddleware.Get(context).Device?.DeviceId,
                detail: $"updated {incoming.Count} notification rule(s)");

            return Results.Ok(new NotificationRulesResponse { Rules = settings.Current.Notifications.Rules });
        });
    }

    // =============================================================================
    // Gaming
    // =============================================================================

    public static void MapGaming(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/gaming/state", ([FromServices] GamingService gaming) => Results.Ok(gaming.GetState()));
        api.MapGet("/gaming/profiles", ([FromServices] GamingService gaming) => Results.Ok(gaming.Profiles()));
        api.MapGet("/gaming/fps", ([FromServices] GamingService gaming) => Results.Ok(new
        {
            fps = gaming.MeasureFps(),
            note = "Windows exposes no supported API for the presented frame rate, so this is null rather than a guess.",
        }));

        api.MapPost("/gaming/mode", (GamingModeRequest request, [FromServices] GamingService gaming) =>
            Results.Ok(gaming.SetMode(request)));

        api.MapPost("/gaming/apps/{id}/state", (string id, AppStateRequest request, [FromServices] GamingService gaming,
            HttpContext context) =>
            Results.Ok(gaming.SetAppState(id, request.Enabled,
                RequestContextMiddleware.Get(context).Device?.DeviceId)));
    }

    // =============================================================================
    // Remote control
    // =============================================================================

    // ============================================================================
    // Camera
    // ============================================================================

    /// <summary>
    /// A phone's camera, exposed two ways: a still frame for the app to poll, and an MJPEG
    /// stream for an outside program to open as a camera source.
    /// <para>
    /// The stream was meant to be unauthenticated, and the argument for that was a real trade
    /// rather than an oversight. MJPEG has no way to carry a per-frame signature, and the
    /// consumers this exists for - OBS, VLC, ffmpeg - cannot add one, so requiring the request
    /// signature would mean only our own client could ever watch it. Anyone who can already
    /// reach the agent over the network is inside the trust boundary this product has always
    /// had; the pairing secret is what protects the machine, and that is unchanged. What is
    /// protected is the camera feed's availability, not its confidentiality.
    /// </para>
    /// <para>
    /// It is not unauthenticated, though. <c>SignatureAuthenticationMiddleware.IsPublic</c>
    /// does not return true for <c>/api/v1/camera/stream.mjpg</c>, and the path is not on the
    /// ticket-authenticated list either, so the request signature is required - and the
    /// external programs named above cannot produce one, which means they cannot open this
    /// stream at all. The comment said one thing and the middleware did the other; the safer
    /// of the two is what has been shipping, and `ScopeTableTests` pins it as
    /// <c>false</c> precisely so the disagreement cannot go unnoticed again. Turning the
    /// stream public is the trade described in the first paragraph, and it has not been
    /// taken: it needs to be decided, not inferred from a comment that was never true.
    /// </para>
    /// <para>
    /// The frames themselves arrive over the signed WebSocket, so a phone cannot push video
    /// without being a paired device with the control scope. Only the reading of the stored
    /// frames is open to argument here, not the writing of it.
    /// </para>
    /// </summary>
    public static void MapCamera(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/camera/state", (HttpContext context, [FromServices] CameraFrameStore cameras) =>
        {
            var deviceId = RequestContextMiddleware.Get(context).Device!.DeviceId;
            var latest = cameras.Latest(deviceId);
            return Results.Ok(new
            {
                live = cameras.IsLive(deviceId),
                width = latest?.Width ?? 0,
                height = latest?.Height ?? 0,
                ageMs = latest is null ? (long?)null : (long)(DateTimeOffset.UtcNow - latest.CapturedAt).TotalMilliseconds,
                streamUrl = $"/api/v1/camera/stream.mjpg",
            });
        });

        api.MapGet("/camera/latest", (HttpContext context, [FromServices] CameraFrameStore cameras) =>
        {
            var deviceId = RequestContextMiddleware.Get(context).Device!.DeviceId;
            var latest = cameras.Latest(deviceId);
            return latest is null ? Results.NotFound() : Results.File(latest.Jpeg, "image/jpeg");
        });

        api.MapGet("/camera/stream.mjpg", async (HttpContext context, [FromServices] CameraFrameStore cameras) =>
        {
            var deviceId = RequestContextMiddleware.Get(context)?.Device?.DeviceId ?? "unknown";

            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

            await using var stream = context.Response.BodyWriter.AsStream();
            var seen = 0L;

            // Poll rather than subscribe. The store is a one-frame slot, so there is nothing to
            // await - the loop simply asks for whatever is newest. A condition variable would wake
            // sooner but needs a second subscription path and a broadcast, for a difference no
            // viewer of a 15fps camera could see.
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var frame = cameras.Latest(deviceId);

                if (frame is null)
                {
                    if (seen != 0)
                    {
                        // Say out loud that the camera stopped rather than closing silently,
                        // which a viewer reads as "the stream ended" and does not retry.
                        await WriteBoundaryAsync(stream, "camera stopped", null);
                        seen = 0;
                    }

                    await Task.Delay(200, context.RequestAborted);
                    continue;
                }

                // Only send a frame when it is a new one, not on every poll.
                if (frame.CapturedAt.UtcTicks != seen)
                {
                    seen = frame.CapturedAt.UtcTicks;
                    await WriteBoundaryAsync(stream, $"{frame.Width}x{frame.Height}", frame.Jpeg);
                    await stream.FlushAsync(context.RequestAborted);
                }

                await Task.Delay(33, context.RequestAborted);
            }
        });

        return;

        static async Task WriteBoundaryAsync(Stream stream, string header, byte[]? jpeg)
        {
            if (jpeg is null)
            {
                await stream.WriteAsync(Ascii("--frame\r\nContent-Type: text/plain\r\n\r\n"));
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(header));
                await stream.WriteAsync("\r\n"u8.ToArray());
                await stream.FlushAsync();
                return;
            }

            await stream.WriteAsync(Ascii("--frame\r\n"));
            await stream.WriteAsync(Ascii($"Content-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n"));
            await stream.WriteAsync(jpeg);
            await stream.WriteAsync(Ascii("\r\n"));
        }

        static byte[] Ascii(string value) => System.Text.Encoding.ASCII.GetBytes(value);
    }

    public static void MapRemote(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        api.MapGet("/remote/session", ([FromServices] RemoteService remote) => Results.Ok(remote.GetStatus()));

        api.MapPost("/remote/input", (HttpContext context, RemoteInputRequest request, [FromServices] RemoteService remote) =>
        {
            var result = remote.SendInput(request, RequestContextMiddleware.Get(context).Device?.DeviceId);
            return result.Accepted ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status409Conflict);
        });

        api.MapDelete("/remote/session", (HttpContext context, [FromServices] RemoteService remote) =>
        {
            remote.StopStream(RequestContextMiddleware.Get(context).Device!.DeviceId);
            return Results.Ok(new { stopped = true });
        });
    }

    // =============================================================================
    // Admin
    // =============================================================================

    public static void MapAdmin(IEndpointRouteBuilder endpoints)
    {
        var api = Api(endpoints);

        // ---- Devices -----------------------------------------------------------
        api.MapGet("/devices", ([FromServices] DeviceStore store) =>
            Results.Ok(new DeviceListResponse
            {
                Devices = store.List().Select(ToDto).ToList(),
            }));

        api.MapPost("/auth/rotate", async (HttpContext context, RotateRequest request,
            [FromServices] PairingManager pairing, [FromServices] RequestVerifier verifier, [FromServices] SettingsStore settings, [FromServices] AuditLog audit) =>
        {
            var ctx = RequestContextMiddleware.Get(context);
            var deviceId = ctx.Device!.DeviceId;

            // Deliberately requires the device id to be echoed back: a rotation is a
            // high-consequence operation and an accidental call should not silently
            // invalidate credentials.
            if (!string.Equals(request.ConfirmDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest($"Set 'confirmDeviceId' to '{deviceId}' to confirm a key rotation.");
            }

            var (device, previousKeyId, newKeyId, wrapped) = pairing.Rotate(deviceId, out _);

            // The nonce cache holds nonces that were signed with the old key; clearing it
            // avoids spurious replay errors for requests still in flight.
            verifier.ResetNonces();

            audit.Record(AuditLog.ActionDeviceRotated, "ok", deviceId, ctx.Device.Name, ctx.RemoteAddressText,
                $"keyId {previousKeyId} -> {newKeyId}");

            return Results.Ok(new RotateResponse
            {
                DeviceId = deviceId,
                PreviousKeyId = previousKeyId,
                KeyId = newKeyId,
                GraceSeconds = ProtocolConstants.KeyRotationGraceSeconds,
                WrappedSecrets = new WrappedSecretDto
                {
                    Nonce = wrapped.NonceBase64,
                    Ciphertext = wrapped.CiphertextBase64,
                    Tag = wrapped.TagBase64,
                },
                // The client holds the only key that can unwrap this, so the response is
                // for the caller to apply locally rather than something the server acts on.
                RequiresManualDelivery = true,
            });
        });

        api.MapPost("/devices/{deviceId}/rotate", (string deviceId, [FromServices] PairingManager pairing, [FromServices] AuditLog audit) =>
        {
            try
            {
                var (device, previousKeyId, newKeyId, wrapped) = pairing.Rotate(deviceId, out _);
                audit.Record(AuditLog.ActionDeviceRotated, "ok", deviceId, detail: $"keyId {previousKeyId} -> {newKeyId}");

                return Results.Ok(new RotateResponse
                {
                    DeviceId = deviceId,
                    PreviousKeyId = previousKeyId,
                    KeyId = newKeyId,
                    GraceSeconds = ProtocolConstants.KeyRotationGraceSeconds,
                    WrappedSecrets = new WrappedSecretDto
                    {
                        Nonce = wrapped.NonceBase64,
                        Ciphertext = wrapped.CiphertextBase64,
                        Tag = wrapped.TagBase64,
                    },
                    RequiresManualDelivery = true,
                });
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(ex.Message);
            }
        });

        api.MapDelete("/devices/{deviceId}", (string deviceId, [FromServices] DeviceStore store, [FromServices] AuditLog audit,
            HttpContext context) =>
        {
            var removed = store.Remove(deviceId);
            if (removed)
            {
                audit.Record(AuditLog.ActionDeviceRevoked, "ok",
                    RequestContextMiddleware.Get(context).Device?.DeviceId, detail: deviceId);
            }

            return removed ? Results.Ok(new { removed }) : NotFound($"No paired device '{deviceId}'.");
        });

        // ---- Settings ----------------------------------------------------------
        api.MapGet("/settings", ([FromServices] SettingsStore settings) => Results.Ok(settings.Current));

        api.MapPut("/settings", async (HttpContext context, SettingsDto incoming, [FromServices] SettingsStore settings,
            [FromServices] LocalNetworkGuard guard, [FromServices] RateLimiter limiter, FileSandbox sandbox,
            [FromServices] NotificationEngine engine, [FromServices] WebSocketHub hub, [FromServices] AuditLog audit) =>
        {
            var (ok, warnings, restartRequired) = await settings.UpdateAsync(incoming).ConfigureAwait(false);

            if (!ok)
            {
                return Results.Json(
                    new Protocol.ErrorResponse { Error = new ErrorBody { Code = ErrorCodes.BadRequest,
                        Message = warnings.FirstOrDefault() ?? "The settings were rejected." } },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Push the new policy into the live components. The rate limits are applied by the
            // hosted service, which re-reads the settings on change; the guard and the sandbox
            // are per-request state and have to be pushed here.
            guard.Configure(settings.Current.Security.LocalNetworkOnly, settings.Current.Security.AllowedSubnets);
            settings.ApplyToSandbox(sandbox);
            engine.Prime(settings.Current.Notifications.Rules);

            audit.Record(AuditLog.ActionSettingsChanged, "ok",
                RequestContextMiddleware.Get(context).Device?.DeviceId,
                detail: $"restart required: {restartRequired}");

            return Results.Ok(new SettingsUpdateResponse
            {
                Ok = true,
                RestartRequired = restartRequired,
                AppliedAt = AgentDatabase.NowUnix(),
                Warnings = warnings,
            });
        });

        api.MapPost("/settings/reset", async (HttpContext context, [FromServices] SettingsStore settings,
            [FromServices] CertificateAuthority authority, [FromServices] DeviceStore devices, [FromServices] AuditLog audit) =>
        {
            // Reset clears the TLS identity, which invalidates every paired device. This is
            // audited at warning level and reported plainly, because it is destructive.
            audit.Record(AuditLog.ActionCertificateReset, "ok",
                RequestContextMiddleware.Get(context).Device?.DeviceId,
                detail: "TLS identity reset; all paired devices must re-pair");

            authority.Reset();
            await settings.ResetAsync().ConfigureAwait(false);

            return Results.Ok(new
            {
                ok = true,
                detail = "The TLS identity was reset and settings were restored to defaults. " +
                         "Every paired device must pair again. Restart the service to finish.",
                restartRequired = true,
            });
        });

        // ---- Logs and audit ----------------------------------------------------
        api.MapGet("/logs", async (string? tail, string? level, long? since, [FromServices] AgentOptions options) =>
        {
            var lines = new List<LogLineDto>();
            var take = Math.Clamp(tail is null ? 200 : int.Parse(tail), 1, 2000);
            var directory = options.ResolveLogDirectory();

            try
            {
                var files = Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "agent-*.log")
                        .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : [];

                foreach (var file in files)
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);

                    var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                    foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        lines.Add(ParseLogLine(line.Trim('\r')));
                    }

                    if (lines.Count >= take)
                        break;
                }
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new Protocol.ErrorResponse { Error = new ErrorBody { Code = ErrorCodes.Internal, Message = ex.Message } },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var filtered = lines
                .Where(l => level is null || l.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                .Where(l => since is null || l.Ts >= since)
                .TakeLast(take)
                .ToList();

            return Results.Ok(new LogTailResponse
            {
                Lines = filtered,
                Truncated = lines.Count > filtered.Count,
                LogDirectory = directory,
            });
        });

        api.MapGet("/audit", ([FromServices] AuditLog audit, int? limit, string? deviceId) =>
        {
            var (valid, brokenAt) = audit.VerifyChain();

            return Results.Ok(new AuditListResponse
            {
                Entries = audit.Tail(limit ?? 200, deviceId).ToList(),
                ChainValid = valid,
            });
        });
    }

    // =============================================================================
    // WebSocket
    // =============================================================================

    public static void MapWebSocket(IEndpointRouteBuilder endpoints)
    {
        endpoints.Map(ProtocolConstants.WebSocketPath, async (HttpContext context, [FromServices] WebSocketHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await ApiError.WriteAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                    "This endpoint requires a WebSocket upgrade.").ConfigureAwait(false);
                return;
            }

            var ctx = RequestContextMiddleware.Get(context);
            if (ctx.Device is null)
            {
                await ApiError.WriteAsync(context, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized,
                    "The WebSocket upgrade must be signed.").ConfigureAwait(false);
                return;
            }

            // The signature for the upgrade covers /ws with an empty body; the query
            // string carries the headers so the signature is not exposed in a URL that
            // could be logged by an intermediary.
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await hub.HandleAsync(context, socket, ctx.Device).ConfigureAwait(false);
        });
    }

    // =============================================================================
    // Shared helpers
    // =============================================================================

    private static IResult Guard<T>(Func<T> action)
    {
        try
        {
            return Results.Ok(action());
        }
        catch (Exception ex) when (ex is SandboxViolationException or FileAccessDeniedException
                                       or UnauthorizedAccessException or IOException
                                       or DirectoryNotFoundException or FileNotFoundException)
        {
            return ApiError.FromFileException(ex);
        }
    }

    private static IResult BadRequest(string message) =>
        ApiError.Result(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, message);

    private static IResult NotFound(string message) =>
        ApiError.Result(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);

    private static List<string> ScopeNames(DeviceScope scopes) =>
        Enum.GetValues<DeviceScope>()
            .Where(s => s != DeviceScope.None && s != DeviceScope.All && (scopes & s) == s)
            .Select(s => s.ToString().ToLowerInvariant())
            .ToList();

    private static PairedDeviceDto ToDto(PairedDevice device) => new()
    {
        DeviceId = device.DeviceId,
        Name = device.Name,
        Platform = device.Platform,
        Scopes = Enum.GetValues<DeviceScope>()
            .Where(s => s != DeviceScope.None && s != DeviceScope.All && (device.Scopes & s) == s)
            .Select(s => s.ToString().ToLowerInvariant())
            .ToList(),
        Enabled = device.Enabled,
        FirstPaired = device.FirstPaired,
        LastSeen = device.LastSeen,
        LastAddress = device.LastAddress,
        KeyId = device.KeyId,
        Suspended = !device.Enabled,
    };

    private static LogLineDto ParseLogLine(string line)
    {
        // Serilog compact format: 2026-09-29 12:00:00.000 +02:00 [INF] Category: message
        var level = "Information";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string? logger = null;
        var message = line;

        var open = line.IndexOf('[', StringComparison.Ordinal);
        var close = open >= 0 ? line.IndexOf(']', open) : -1;

        if (open > 0 && close > open)
        {
            level = line[(open + 1)..close] switch
            {
                "INF" => "Information",
                "WRN" => "Warning",
                "ERR" => "Error",
                "DBG" => "Debug",
                "VRB" => "Verbose",
                "FTL" => "Fatal",
                var other => other,
            };

            if (DateTimeOffset.TryParse(line[..open].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                ts = parsed.ToUnixTimeSeconds();
            }

            var rest = line[(close + 1)..].TrimStart();
            var colon = rest.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0)
            {
                logger = rest[..colon].Trim();
                message = rest[(colon + 1)..].Trim();
            }
        }

        return new LogLineDto { Ts = ts, Level = level, Logger = logger, Message = message };
    }

}

/// <summary>Name constants for the privileges the service enables on demand.</summary>
public static class PrivilegeHelperNames
{
    public const string Debug = Esi.Platform.Native.WinConst.SE_DEBUG_NAME;
}
