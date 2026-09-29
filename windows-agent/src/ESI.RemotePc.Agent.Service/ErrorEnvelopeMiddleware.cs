using System.Net;
using Esi.Platform.Machine;

namespace Esi.Service;

/// <summary>
/// Guarantees that every failing response carries a body in the agent's error envelope
/// (<c>docs/PROTOCOL.md</c> §4.4).
/// <para>
/// This exists because several framework-generated failures are status-code-only. When
/// <c>RequestDelegateFactory</c> fails to parse a request body it sets 400 and returns
/// without writing anything; an unsupported content type gets the same treatment at 415; an
/// unmatched route is 404 and a wrong verb is 405, both empty. On the phone those are
/// indistinguishable from a connection that was cut mid-response, which turns a diagnosable
/// client bug into a mystery. A handler that already answered — including every
/// <see cref="ApiError"/> path — is left untouched.
/// </para>
/// <para>
/// It must be the last middleware in the pipeline so the whole request has been handled by
/// the time it inspects the response.
/// </para>
/// </summary>
public sealed class ErrorEnvelopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuditLog audit)
    {
        await next(context).ConfigureAwait(false);

        // Anything already flushed is immutable, and a handler that wrote a body has set a
        // content type. Both mean "somebody already answered".
        if (context.Response.HasStarted || context.Response.ContentType is not null)
            return;

        var status = context.Response.StatusCode;
        if (status < 400)
            return;

        var requestId = context.Items.TryGetValue(nameof(RequestContext), out var raw) &&
                         raw is RequestContext ctxState
            ? ctxState.RequestId
            : "";

        var (code, message) = Describe(context, status);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = null;

        await context.Response.WriteAsJsonAsync(new ErrorResponse
        {
            Error = new ErrorBody
            {
                Code = code,
                Message = message,
                RequestId = requestId,
                Details = new Dictionary<string, string>
                {
                    ["method"] = context.Request.Method,
                    ["path"] = context.Request.Path.Value ?? "/",
                },
            },
        }, ProtocolJson.Options).ConfigureAwait(false);

        audit.Record(AuditLog.ActionRequestRejected, "empty-body", remoteAddress: RemoteOf(context),
            detail: $"{context.Request.Method} {context.Request.Path} produced an empty {status}");
    }

    /// <summary>
    /// Names the failure precisely. A wrong verb and a wrong path are the two most common
    /// causes of a client bug, and they are worth distinguishing at the call site.
    /// </summary>
    private static (string Code, string Message) Describe(HttpContext context, int status) => status switch
    {
        StatusCodes.Status400BadRequest => (ErrorCodes.BadRequest,
            "The request body could not be read as the expected JSON. Check the payload and its Content-Type."),

        StatusCodes.Status404NotFound => (ErrorCodes.NotFound,
            $"No route matches {context.Request.Method} {context.Request.Path}. " +
            "This usually means the phone and the agent disagree about the protocol version."),

        StatusCodes.Status405MethodNotAllowed => (ErrorCodes.PathNotAllowed,
            $"{context.Request.Method} is not supported for this route. " +
            "Check the HTTP verb in the client."),

        StatusCodes.Status413PayloadTooLarge => (ErrorCodes.UploadTooLarge,
            "The request body is larger than the agent accepts. Use an upload ticket for large files."),

        StatusCodes.Status415UnsupportedMediaType => (ErrorCodes.BadRequest,
            "The request Content-Type is not supported. Send application/json, or " +
            "application/octet-stream for a file upload."),

        _ => (ErrorCodes.Internal, "The agent could not complete the request."),
    };

    private static string RemoteOf(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        return remote?.ToString() ?? "unknown";
    }
}
