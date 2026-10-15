using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Esi.Platform.Files;
using Esi.Platform.Machine;
using Microsoft.Extensions.Primitives;

namespace Esi.Service;

/// <summary>Per-request ambient state, populated by the first middleware.</summary>
public sealed class RequestContext
{
    public string RequestId { get; init; } = "";
    public IPAddress? RemoteAddress { get; init; }
    public string RemoteAddressText => RemoteAddress?.ToString() ?? "unknown";

    /// <summary>The authenticated device, or <c>null</c> for unauthenticated routes.</summary>
    public PairedDevice? Device { get; set; }

    public int KeyId { get; set; }

    /// <summary>Raw request body, buffered so the signature can be verified over it.</summary>
    public byte[] Body { get; set; } = [];
    public long StartedTimestampMs { get; init; } = Stopwatch.GetTimestamp() / TimeSpan.TicksPerMillisecond;
}

/// <summary>
/// Attaches a request id, the remote address and the authenticated device to the request,
/// and buffers the body so the signature can be verified over the exact bytes received.
/// </summary>
public sealed class RequestContextMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Bodies larger than this are not buffered for signing. They are limited to
    /// <see cref="UploadTicketStore"/>-authenticated transfers, which authenticate with a
    /// single-use ticket instead of an HMAC.
    /// </summary>
    private const int MaxBufferedBodyBytes = ProtocolConstants.MaxSignedBodyBytes;

    public async Task InvokeAsync(HttpContext context, LocalNetworkGuard networkGuard)
    {
        var remote = context.Connection.RemoteIpAddress;

        // A proxy would present a loopback address; the agent does not support one, and
        // trusting X-Forwarded-For would let any client spoof its source subnet.
        if (remote is not null && remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();

        var requestId = EsiCrypto.Base64Url(RandomNumberGenerator.GetBytes(9));

        context.Items[nameof(RequestContext)] = new RequestContext
        {
            RequestId = requestId,
            RemoteAddress = remote,
        };

        context.Response.Headers["X-ESI-Request-Id"] = requestId;

        await BufferBodyAsync(context).ConfigureAwait(false);

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffers the request body so the signature can be computed over exactly what arrived,
    /// and rewinds it so the endpoint's input formatter still sees it.
    /// <para>
    /// Rewindability comes from <c>EnableBuffering</c> rather than from swapping
    /// <see cref="HttpRequest.Body"/> for a <see cref="MemoryStream"/>. Minimal API's JSON
    /// input formatter reads through the request's pipe reader, which is captured over
    /// whatever stream was current when it was first materialised; replacing the stream
    /// afterwards leaves that reader at end-of-stream, so every body-bound parameter binds
    /// as absent and the endpoint rejects its own request with a body-less model-state 400.
    /// </para>
    /// <para>
    /// Bodies over the signing cap are left alone. Those are the file-upload path, which
    /// authenticates with a single-use ticket rather than an HMAC and must stream to disk
    /// instead of being held in memory.
    /// </para>
    /// </summary>
    private async Task BufferBodyAsync(HttpContext context)
    {
        var request = context.Request;

        if (request.ContentLength is 0)
            return;

        // A declared length over the cap is left alone: buffering it would both stall on a
        // body that will never be signed and defeat the streaming upload path.
        if (request.ContentLength is { } declared && declared > MaxBufferedBodyBytes)
            return;

        if (!CanCarryBody(request.Method))
            return;

        var buffered = false;

        try
        {
            // The threshold keeps ordinary control requests in memory; only a large upload
            // would spill to a temp file, and those are excluded above anyway.
            request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: MaxBufferedBodyBytes);
            buffered = true;
        }
        catch (IOException)
        {
            // A body already consumed elsewhere cannot be rewound. Nothing downstream can
            // make sense of it, and every endpoint here reads the body itself.
            return;
        }

        byte[] body;

        using (var memory = new MemoryStream())
        {
            await request.Body.CopyToAsync(memory, context.RequestAborted).ConfigureAwait(false);
            body = memory.ToArray();
        }

        var ctx = Get(context);
        ctx.Body = body;
        context.Items[nameof(RequestContext)] = ctx;

        if (body.Length == 0)
        {
            if (buffered)
                request.Body.Position = 0;
            return;
        }

        // The signature is over these exact bytes, and so is everything the endpoint parses.
        // The stream is shared with the pipe reader, so rewinding it here is what makes the
        // buffered bytes visible to the input formatter.
        request.Body.Position = 0;
    }

    private static bool CanCarryBody(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    public static RequestContext Get(HttpContext context) =>
        (RequestContext)context.Items[nameof(RequestContext)]!;
}

/// <summary>
/// Rejects requests that do not come from an allowed network.
/// <para>
/// This is the first real check and it runs before authentication so an off-LAN packet
/// never reaches the device table. Loopback is always allowed because the per-user session
/// agent talks to the agent over <c>127.0.0.1</c>.
/// </para>
/// </summary>
public sealed class LocalNetworkMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, LocalNetworkGuard guard, AuditLog audit)
    {
        var ctx = RequestContextMiddleware.Get(context);

        if (!guard.IsAllowed(ctx.RemoteAddress))
        {
            audit.Record(
                AuditLog.ActionNetworkRejected,
                "denied",
                remoteAddress: ctx.RemoteAddressText,
                detail: $"{context.Request.Method} {context.Request.Path}");

            await ApiError.WriteAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.ForbiddenNetwork,
                "This agent only accepts connections from the local network.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Verifies the request signature and enforces scopes and rate limits.
/// <para>
/// Routes under <c>/api/v1/handshake</c>, <c>/api/v1/pair/*</c>, <c>/api/v1/health</c> and
/// the pairing landing page are public. <c>/api/v1/files/upload</c> authenticates with a
/// single-use ticket instead, because signing a multi-megabyte body would mean hashing it
/// on every hop for no security gain the ticket does not already provide. Everything else
/// requires a valid signature.
/// </para>
/// </summary>
public sealed class SignatureAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        RequestVerifier verifier,
        RateLimiter limiter,
        LocalNetworkGuard guard,
        SettingsStore settings,
        DeviceStore devices,
        AuditLog audit,
        RequestSignatureValidator signatureValidator)
    {
        var path = context.Request.Path.Value ?? "/";

        // ---- Routing has already run, so a path that matches nothing can be answered as
        // "not found" instead of being misreported as an authentication failure. A phone
        // calling a route the agent does not have is a version mismatch, and it needs to be
        // able to tell that apart from a rejected signature.
        if (context.GetEndpoint() is null)
        {
            await ApiError.WriteAsync(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound,
                    $"No route matches {context.Request.Method} {path}. " +
                    "This usually means the phone and the agent disagree about the protocol version.")
                .ConfigureAwait(false);
            return;
        }

        if (IsPublic(path))
        {
            // Unauthenticated endpoints get their own, much tighter budget. The one
            // exception is the pairing poll, which the protocol requires a phone to make
            // every pollIntervalMs for the whole pairing window; the general budget is far
            // too small for that and would fail any pairing the user did not approve
            // instantly. See RateLimiter.PairingPollsPerMinute.
            var bucket = path.StartsWith("/api/v1/pair/session", StringComparison.OrdinalIgnoreCase)
                ? "pairpoll"
                : "unauth";

            if (!limiter.TryConsumeForAddress(RequestContextMiddleware.Get(context).RemoteAddress, bucket))
            {
                await ApiError.WriteAsync(context, StatusCodes.Status429TooManyRequests,
                    ErrorCodes.RateLimited,
                    bucket == "pairpoll"
                        ? "This phone is polling the pairing attempt faster than the agent allows. "
                          + "Wait for the countdown to finish and try again."
                        : "Too many unauthenticated requests.").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        // The raw upload carries no signature: it is authenticated by the single-use ticket
        // in its query string, which the endpoint validates before touching the body. It is
        // not public, though — the local-network filter above has already run.
        if (IsTicketAuthenticated(path))
        {
            if (!limiter.TryConsumeForAddress(RequestContextMiddleware.Get(context).RemoteAddress, "upload"))
            {
                await ApiError.WriteAsync(context, StatusCodes.Status429TooManyRequests,
                    ErrorCodes.RateLimited, "Too many upload attempts from this address.").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        var ctx = RequestContextMiddleware.Get(context);
        var body = ctx.Body;

        var result = verifier.Verify(
            new HttpHeaderAccessor(context.Request.Headers),
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            context.Request.QueryString.HasValue ? context.Request.QueryString.Value!.TrimStart('?') : null,
            body,
            settings.Current.Security.ClockSkewSeconds);

        if (!result.Succeeded || result.Device is null)
        {
            audit.Record(
                AuditLog.ActionAuthFailed,
                "denied",
                deviceId: context.Request.Headers["X-ESI-Device-Id"].ToString() is { Length: > 0 } id ? id : null,
                remoteAddress: ctx.RemoteAddressText,
                detail: $"{context.Request.Method} {path}: {result.ErrorCode} — {result.ErrorMessage}");

            var status = result.ErrorCode switch
            {
                ErrorCodes.Replay or ErrorCodes.Timestamp or ErrorCodes.KeyId => StatusCodes.Status401Unauthorized,
                ErrorCodes.ForbiddenScope => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status401Unauthorized,
            };

            await ApiError.WriteAsync(context, status, result.ErrorCode, result.ErrorMessage ?? "Unauthorized.")
                .ConfigureAwait(false);
            return;
        }

        // ---- Scope enforcement ---------------------------------------------------
        var required = signatureValidator.RequiredScope(path, context.Request.Method);
        if (required != DeviceScope.None && !result.Device.Has(required))
        {
            audit.Record(
                AuditLog.ActionAuthFailed,
                "forbidden",
                result.Device.DeviceId,
                result.Device.Name,
                ctx.RemoteAddressText,
                $"{context.Request.Method} {path} requires scope {required}");

            await ApiError.WriteAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.ForbiddenScope,
                $"This device is not permitted to use {path} (needs the {required} scope).").ConfigureAwait(false);
            return;
        }

        // ---- Rate limiting -------------------------------------------------------
        var category = IsPowerRoute(path) ? "power" : "api";
        if (!limiter.TryConsumeForDevice(result.Device.DeviceId, category))
        {
            var retryAfter = limiter.RetryAfterSeconds($"device:{result.Device.DeviceId}:{category}",
                category == "power" ? limiter.PowerActionsPerMinute : limiter.RequestsPerMinute);

            context.Response.Headers.RetryAfter = retryAfter.ToString();
            audit.Record(AuditLog.ActionRateLimited, "throttled", result.Device.DeviceId, result.Device.Name,
                ctx.RemoteAddressText, $"{context.Request.Method} {path}");

            await ApiError.WriteAsync(context, StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited,
                $"Rate limit reached. Retry in {retryAfter} second(s).").ConfigureAwait(false);
            return;
        }

        if (!limiter.TryConsumeForAddress(ctx.RemoteAddress, "api"))
        {
            context.Response.Headers.RetryAfter = "5";
            await ApiError.WriteAsync(context, StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited,
                "Too many requests from this address.").ConfigureAwait(false);
            return;
        }

        ctx.Device = result.Device;
        ctx.KeyId = result.KeyId;
        devices.TouchSeen(result.Device.DeviceId, ctx.RemoteAddressText, result.KeyId);

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The unauthenticated surface: what a request with no signature at all can reach.
    ///
    /// Internal rather than private purely so `ScopeTableTests` can pin this list. It is the
    /// most security-critical table in the product - everything here is reachable by anyone
    /// who can open a socket - and until it was reachable from a test it was covered by
    /// nothing, which is how a list meant to be short can grow one path at a time.
    /// </summary>
    internal static bool IsPublic(string path)
    {
        if (path == "/") return true;
        if (path.Equals("/api/v1/handshake", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/v1/health", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/api/v1/pair/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/pairing", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Routes authenticated by a single-use ticket rather than a request signature. Exact
    /// match, not a prefix: <c>/api/v1/files/upload-ticket</c> issues the ticket and is
    /// signed like any other request.
    /// </summary>
    private static bool IsTicketAuthenticated(string path) =>
        path.Equals("/api/v1/files/upload", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerRoute(string path) =>
        path.StartsWith("/api/v1/power", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Adapts ASP.NET header collections to the verifier's abstraction.</summary>
public sealed class HttpHeaderAccessor(IHeaderDictionary headers) : IHeaderDictionaryLike
{
    public string? Get(string name) => headers.TryGetValue(name, out StringValues values) ? values.ToString() : null;
}

/// <summary>
/// Maps a route to the scope it requires. Kept as a table rather than scattered
/// <c>RequireAuthorization</c>-style attributes so the whole policy is readable in one
/// place and testable without spinning up a server.
/// </summary>
public sealed class RequestSignatureValidator
{
    /// <summary>
    /// Maps a route to the scope it requires.
    /// <para>
    /// The whole policy, in one reviewable and testable place. The ordering matters: the
    /// specific prefixes are tested before the general ones, so a mutating route under a
    /// read-mostly prefix is never caught by the default.
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Administrative routes (device management, settings, logs, audit,
    ///   key rotation) require <c>admin</c>.</description></item>
    ///   <item><description>The file manager requires <c>files</c> for every verb: reading a
    ///   directory tree is about as sensitive as writing to it.</description></item>
    ///   <item><description>Control routes (power, app launch, media, remote input, gaming)
    ///   need <c>read</c> for GET/HEAD and <c>control</c> for anything that changes state.</description></item>
    ///   <item><description>Everything else needs <c>read</c>.</description></item>
    /// </list>
    /// </summary>
    public DeviceScope RequiredScope(string path, string method)
    {
        var isReadOnly = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
                         method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        // Most specific prefixes first.
        if (StartsWith(path, "/api/v1/auth/rotate") || StartsWith(path, "/api/v1/devices") ||
            StartsWith(path, "/api/v1/settings") || StartsWith(path, "/api/v1/logs") ||
            StartsWith(path, "/api/v1/audit"))
        {
            return DeviceScope.Admin;
        }

        if (StartsWith(path, "/api/v1/files"))
            return DeviceScope.Files;

        // The WebSocket upgrade. The upgrade itself is only a read, but everything multiplexed
        // over the socket is not, so the connection opens at the lowest scope the client can
        // legitimately have and each frame is checked again in the hub.
        if (StartsWith(path, ProtocolConstants.WebSocketPath))
            return DeviceScope.Read;

        if (StartsWith(path, "/api/v1/power") || StartsWith(path, "/api/v1/apps") ||
            StartsWith(path, "/api/v1/media") || StartsWith(path, "/api/v1/remote") ||
            StartsWith(path, "/api/v1/gaming"))
        {
            return isReadOnly ? DeviceScope.Read : DeviceScope.Control;
        }

        // Terminating a process is a control action, not a read. This prefix is otherwise
        // read-mostly, so without an explicit case the default below would let a device
        // holding only `read` kill any process on the machine - the service runs with
        // SeDebugPrivilege, so "read" would mean SYSTEM.
        if (StartsWith(path, "/api/v1/system/process"))
            return isReadOnly ? DeviceScope.Read : DeviceScope.Control;

        return DeviceScope.Read;
    }

    private static bool StartsWith(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Persists a compact record of every handled request for diagnostics.</summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AgentDatabase database, ILogger<RequestLoggingMiddleware> log)
    {
        var stopwatch = Stopwatch.StartNew();
        var ctx = RequestContextMiddleware.Get(context);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            var elapsed = stopwatch.ElapsedMilliseconds;

            var level = context.Response.StatusCode switch
            {
                >= 500 => LogLevel.Error,
                >= 400 => LogLevel.Warning,
                _ => LogLevel.Information,
            };

            log.Log(level,
                "{Method} {Path} -> {Status} in {Elapsed}ms from {Remote} as {Device}",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                elapsed,
                ctx.RemoteAddressText,
                ctx.Device?.DeviceId ?? "anonymous");

            // Fire-and-forget: a logging failure must not change the response.
            _ = RecordAsync(database, context, ctx, elapsed);
        }
    }

    private static async Task RecordAsync(AgentDatabase database, HttpContext context, RequestContext ctx, long elapsed)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO request_log (ts, method, path, device_id, remote, status, duration_ms)
                VALUES ($ts, $method, $path, $device, $remote, $status, $duration);
                """;
            command.Parameters.AddWithValue("$ts", AgentDatabase.NowUnix());
            command.Parameters.AddWithValue("$method", context.Request.Method);
            command.Parameters.AddWithValue("$path", (context.Request.Path.Value ?? "/")[..Math.Min(256, (context.Request.Path.Value ?? "/").Length)]);
            command.Parameters.AddWithValue("$device", AgentDatabase.DbValue(ctx.Device?.DeviceId));
            command.Parameters.AddWithValue("$remote", AgentDatabase.DbValue(ctx.RemoteAddressText));
            command.Parameters.AddWithValue("$status", context.Response.StatusCode);
            command.Parameters.AddWithValue("$duration", elapsed);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must never surface as a request failure.
        }
    }
}

/// <summary>Uniform error envelope, matching the shape in <c>docs/PROTOCOL.md</c> §4.4.</summary>
public static class ApiError
{
    /// <summary>Builds the JSON result for an error status. Preferred over writing directly.</summary>
    public static IResult Result(int status, string code, string message,
        Dictionary<string, string>? details = null, string? requestId = null)
        => Results.Json(new ErrorResponse
        {
            Error = new ErrorBody
            {
                Code = code,
                Message = message,
                RequestId = requestId,
                Details = details,
            },
        }, statusCode: status, options: ProtocolJson.Options);

    /// <summary>Writes the error directly, for handlers that are already streaming a response.</summary>
    public static async Task WriteAsync(HttpContext context, int status, string code, string message,
        Dictionary<string, string>? details = null)
    {
        if (context.Response.HasStarted)
            return;

        var requestId = RequestContextMiddleware.Get(context).RequestId;

        // Response.Clear() also discards the headers, which would take the correlation id
        // with it. It is captured first and re-applied: the whole point of putting the id on
        // the response is that a user can read it out of a browser or a packet capture and
        // quote it, and an error path is exactly when they need to.
        context.Response.Clear();
        context.Response.Headers["X-ESI-Request-Id"] = requestId;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsJsonAsync(new ErrorResponse
        {
            Error = new ErrorBody
            {
                Code = code,
                Message = message,
                RequestId = requestId,
                Details = details,
            },
        }, ProtocolJson.Options).ConfigureAwait(false);
    }

    /// <summary>Maps a file-service exception onto the right status and error code.</summary>
    public static IResult FromFileException(Exception exception) => exception switch
    {
        SandboxViolationException sandbox => Result(StatusCodes.Status403Forbidden, sandbox.Code, sandbox.Message),
        FileAccessDeniedException denied => Result(StatusCodes.Status403Forbidden, denied.Code, denied.Message),
        UnauthorizedAccessException denied => Result(StatusCodes.Status403Forbidden,
            ErrorCodes.ForbiddenScope, $"Windows denied access: {denied.Message}"),
        DirectoryNotFoundException => Result(StatusCodes.Status404NotFound, ErrorCodes.NotFound, exception.Message),
        FileNotFoundException => Result(StatusCodes.Status404NotFound, ErrorCodes.NotFound, exception.Message),
        IOException io => Result(StatusCodes.Status500InternalServerError, ErrorCodes.Internal, io.Message),
        _ => Result(StatusCodes.Status500InternalServerError, ErrorCodes.Internal, exception.Message),
    };
}
