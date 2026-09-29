using System.Reflection;
using System.Text.Json.Serialization;
using Esi.Platform.Apps;
using Esi.Platform.Capture;
using Esi.Platform.Discovery;
using Esi.Platform.Machine;
using Esi.Platform.Media;
using Esi.Platform.Power;
using Esi.Platform.Session;
using Esi.Platform.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Esi.Service;

/// <summary>
/// Composition root. Every module is registered here, which makes this the one place to
/// look to understand what the agent consists of.
/// <para>
/// Registration order matters in two places: the request pipeline is wired in the order
/// the checks must run, and the TLS certificate is resolved before Kestrel starts so a
/// generation failure is visible in the log rather than surfacing as a mystery TLS error
/// on the first request.
/// </para>
/// </summary>
public static class AgentHost
{
    public static async Task Main(string[] args)
    {
        var isService = WindowsServiceHelpers.IsWindowsService();

        var agentOptions = new AgentOptions { Version = ReadVersion() };
        // WebApplication.CreateBuilder (not Host.CreateApplicationBuilder) because the agent
        // needs the full middleware pipeline: routing, WebSockets and endpoint mapping.
        var builder = CreateBuilder(args, agentOptions);

        ConfigureLogging(builder, agentOptions);

        if (isService)
        {
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = ProtocolConstants.ServiceName;
            });
        }

        RegisterServices(builder, agentOptions);
        ConfigureKestrel(builder);

        var app = builder.Build();

        // The schema is created here rather than only in the hosted service, because the
        // startup banner below already reads the agent identity, and several services
        // resolve their dependencies before the host begins running.
        app.Services.GetRequiredService<AgentDatabase>().EnsureCreated();

        ConfigurePipeline(app);

        try
        {
            if (isService)
            {
                Log.Information("ESI Remote PC agent starting as a Windows service");
                await app.RunAsync().ConfigureAwait(false);
            }
            else
            {
                await RunInteractiveAsync(app, agentOptions).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The agent terminated unexpectedly");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the application without running it.
    /// <para>
    /// Extracted so the integration tests can drive the real composition root - the same DI
    /// registrations, the same pipeline, the same Kestrel configuration - instead of a
    /// parallel test host. A test that builds its own DI container proves the container
    /// works; only this proves the product does.
    /// </para>
    /// </summary>
    public static WebApplication BuildApplication(
        string[] args,
        AgentOptions? optionsOverride = null,
        int? portOverride = null)
    {
        var agentOptions = optionsOverride ?? new AgentOptions { Version = ReadVersion() };
        var builder = CreateBuilder(args, agentOptions);

        ConfigureLogging(builder, agentOptions);
        RegisterServices(builder, agentOptions, portOverride);
        ConfigureKestrel(builder);

        var app = builder.Build();
        app.Services.GetRequiredService<AgentDatabase>().EnsureCreated();
        ConfigurePipeline(app);

        return app;
    }

    private static WebApplicationBuilder CreateBuilder(string[] args, AgentOptions agentOptions)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables("ESI_");

        builder.Configuration.GetSection(AgentOptions.SectionName).Bind(agentOptions);
        ApplyCommandLineOverrides(agentOptions, args);
        builder.Services.AddSingleton(agentOptions);

        return builder;
    }

    // =============================================================================
    // Logging
    // =============================================================================

    private static void ConfigureLogging(WebApplicationBuilder builder, AgentOptions options)
    {
        var logDirectory = options.ResolveLogDirectory();
        Directory.CreateDirectory(logDirectory);

        var minimumLevel = options.DebugMode || options.Settings.Debug.VerboseLogging
            ? LogEventLevel.Debug
            : LogEventLevel.Information;

        // Serilog rather than the built-in providers: a Windows service's primary sink is
        // a file, and a rolling daily file with a bounded retention is what makes a
        // long-lived service auditable without filling the disk.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDirectory, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Clamp(options.Settings.Debug.LogRetentionDays, 1, 90),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);
    }

    // =============================================================================
    // Dependency injection
    // =============================================================================

    private static void RegisterServices(WebApplicationBuilder builder, AgentOptions options, int? portOverride = null)
    {
        // ---- Core: identity, configuration, storage ----
        builder.Services.AddSingleton<AgentDatabase>();
        builder.Services.AddSingleton<CertificateAuthority>();
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<IdentityService>();
        builder.Services.AddSingleton<HandshakeBuilder>();
        builder.Services.AddSingleton<AgentClock>();

        // ---- Security ----
        builder.Services.AddSingleton<DeviceStore>();
        builder.Services.AddSingleton<AuditLog>();
        builder.Services.AddSingleton<RequestVerifier>();
        builder.Services.AddSingleton<RateLimiter>();
        builder.Services.AddSingleton<LocalNetworkGuard>();
        builder.Services.AddSingleton<RequestSignatureValidator>();

        // Kestrel's tuning object is a singleton so the pairing manager can read the port the
        // listener actually bound. Registering it here, rather than in ConfigureKestrel, is
        // what keeps the two entry points - Main and BuildApplication - building the same
        // container: a service registered on only one of them is a service that works in the
        // integration tests and is missing in production.
        var kestrel = new KestrelTuning(options, portOverride);
        builder.Services.AddSingleton(kestrel);
        builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>>(kestrel);

        builder.Services.AddSingleton(provider => new PairingManager(
            provider.GetRequiredService<ILogger<PairingManager>>(),
            provider.GetRequiredService<AgentDatabase>(),
            provider.GetRequiredService<DeviceStore>(),
            provider.GetRequiredService<SettingsStore>(),
            provider.GetRequiredService<ISessionAgentBridge>())
        {
            // Read at resolution time, after Kestrel has been configured. A QR carrying the
            // wrong port is a pairing that never connects, with no error reported anywhere.
            Port = provider.GetRequiredService<KestrelTuning>().Port,
        });

        // ---- Notifications ----
        builder.Services.AddSingleton<NotificationStore>();
        builder.Services.AddSingleton<NotificationEngine>();

        // ---- Platform: telemetry ----
        builder.Services.AddSingleton<WmiClient>();
        builder.Services.AddSingleton<SystemInfoProvider>();
        builder.Services.AddSingleton<NetworkProvider>();
        builder.Services.AddSingleton<PdhQuery>();
        builder.Services.AddSingleton<SystemSampler>(sp => new SystemSampler(
            sp.GetRequiredService<ILogger<SystemSampler>>(),
            sp.GetRequiredService<PdhQuery>(),
            options.Settings.Metrics.CollectGpu,
            options.Settings.Metrics.CollectTemperatures));
        builder.Services.AddSingleton<NvmlProvider>();
        builder.Services.AddSingleton<TemperatureProvider>();
        builder.Services.AddSingleton<MetricsService>();
        builder.Services.AddSingleton<GpuTelemetrySummary>();

        // ---- Platform: control ----
        builder.Services.AddSingleton<PrivilegeHelper>();
        builder.Services.AddSingleton<WakeOnLanSender>();
        builder.Services.AddSingleton<PowerService>();
        builder.Services.AddSingleton<MediaController>();
        builder.Services.AddSingleton<AppLauncher>();
        builder.Services.AddSingleton<Esi.Platform.Input.InputInjector>();

        // ---- Features ----
        builder.Services.AddSingleton<Esi.Platform.Files.FileSandbox>();

        // ActivatorUtilities rather than GetRequiredService<FileService>() inside the
        // factory: the latter asks the container for the very type it is in the middle of
        // constructing, which is a circular dependency. The first request to a /files route
        // would then fail - or deadlock - and nothing else in the agent would notice.
        builder.Services.AddSingleton<FileService>(sp =>
            ActivatorUtilities.CreateInstance<FileService>(sp).Initialise());

        builder.Services.AddSingleton<AppService>();
        builder.Services.AddSingleton<GamingService>();
        builder.Services.AddSingleton<RemoteService>();
        builder.Services.AddSingleton<UploadTicketStore>();
        builder.Services.AddSingleton<GdiScreenCaptureProvider>();

        // ---- The interactive session bridge ----
        builder.Services.Configure<Esi.Platform.Session.SessionOptions>(sessionOptions =>
        {
            sessionOptions.AgentPath = options.SessionAgentPath;
            sessionOptions.DataDirectory = options.ResolveDataDirectory();
        });
        // Registered as both the interface and the concrete type: the hosted service needs
        // the concrete bridge in order to call Start(), and the health endpoint reports the
        // concrete session-agent state.
        builder.Services.AddSingleton<SessionAgentBridge>();
        builder.Services.AddSingleton<ISessionAgentBridge>(provider => provider.GetRequiredService<SessionAgentBridge>());

        // ---- Discovery ----
        // The responder takes DiscoveryOptions directly rather than IOptions<T>, so the
        // concrete instance is registered alongside the options binding.
        builder.Services.Configure<DiscoveryOptions>(discoveryOptions =>
        {
            discoveryOptions.Enabled = options.Settings.DiscoveryEnabled;
            discoveryOptions.Port = options.Settings.DiscoveryPort;
        });
        builder.Services.AddSingleton(_ => new DiscoveryOptions
        {
            Enabled = options.Settings.DiscoveryEnabled,
            Port = options.Settings.DiscoveryPort,
        });
        builder.Services.AddSingleton<MdnsResponder>();

        // The responder is built through a factory delegate because the handshake payload
        // pulls in the session bridge and the device store, neither of which should be
        // constructed until discovery actually starts.
        builder.Services.AddSingleton(provider => new UdpDiscoveryResponder(
            provider.GetRequiredService<ILogger<UdpDiscoveryResponder>>(),
            provider.GetRequiredService<DiscoveryOptions>(),
            provider.GetRequiredService<HandshakeBuilder>().Build));

        // ---- Streaming ----
        builder.Services.AddSingleton<WebSocketHub>();

        // ---- Hosted work ----
        builder.Services.AddHostedService<AgentHostedService>();
    }

    // =============================================================================
    // Kestrel
    // =============================================================================

    /// <summary>
    /// Wires the Kestrel options. The instance itself is registered by
    /// <see cref="RegisterServices"/>, which is where the pairing manager can reach it.
    /// </summary>
    private static void ConfigureKestrel(WebApplicationBuilder builder)
    {
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(json =>
        {
            // The HTTP JSON options are derived from the protocol options so a DTO
            // serialises and deserialises identically. Anything less risks a request body
            // failing to bind while the same DTO serialises fine on the way out.
            var target = json.SerializerOptions;
            target.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            target.NumberHandling = JsonNumberHandling.AllowReadingFromString;
            target.PropertyNameCaseInsensitive = true;
            target.ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip;
            target.AllowTrailingCommas = true;

            foreach (var converter in ProtocolJson.Options.Converters)
                target.Converters.Add(converter);
        });

        // Minimal API's automatic 400 answers with a bare, body-less ProblemDetails document.
        // The phone's error handling is built on the error envelope in PROTOCOL.md 4.4, and a
        // body-less 400 is indistinguishable from a transport failure on the client side. The
        // filter is kept enabled - a bind failure is a real failure and must not be swallowed -
        // but the writer behind it is swapped for one that emits the envelope, so a client
        // always gets a code to branch on and the log always names the offending field.
        builder.Services.AddSingleton<IProblemDetailsService, EsiProblemDetailsService>();
    }

    // =============================================================================
    // Pipeline
    // =============================================================================

    private static void ConfigurePipeline(WebApplication app)
    {
        // Order is the security policy, in the order a request meets it:
        //   1. context + body buffering   (so the signature can cover the exact bytes)
        //   2. local-network filter       (cheap, and before any database read)
        //   3. routing                    (so authentication can tell a missing route
        //                                  from a rejected signature)
        //   4. signature + scope + limits
        //   5. logging
        //   6. error envelope             (last, so it sees every produced status)
        app.UseMiddleware<RequestContextMiddleware>();
        app.UseMiddleware<LocalNetworkMiddleware>();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.UseRouting();

        app.UseMiddleware<SignatureAuthenticationMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseMiddleware<ErrorEnvelopeMiddleware>();

        app.UseEndpoints(endpoints =>
        {
            Endpoints.MapPublic(endpoints);
            Endpoints.MapSystem(endpoints);
            Endpoints.MapPower(endpoints);
            Endpoints.MapApps(endpoints);
            Endpoints.MapFiles(endpoints);
            Endpoints.MapMedia(endpoints);
            Endpoints.MapNotifications(endpoints);
            Endpoints.MapGaming(endpoints);
            Endpoints.MapRemote(endpoints);
            Endpoints.MapAdmin(endpoints);
            Endpoints.MapWebSocket(endpoints);
        });
    }

    // =============================================================================
    // Console mode
    // =============================================================================

    private static async Task RunInteractiveAsync(WebApplication app, AgentOptions options)
    {
        var settings = app.Services.GetRequiredService<SettingsStore>();
        var authority = app.Services.GetRequiredService<CertificateAuthority>();
        var identity = app.Services.GetRequiredService<IdentityService>();
        var sandbox = app.Services.GetRequiredService<Esi.Platform.Files.FileSandbox>();

        Log.Information("ESI Remote PC agent {Version} — console mode", options.Version);
        Log.Information("  device id   : {DeviceId}", identity.DeviceId);
        Log.Information("  data dir    : {Data}", options.ResolveDataDirectory());
        Log.Information("  log dir     : {Logs}", options.ResolveLogDirectory());
        Log.Information("  https port  : {Port} (TLS {Tls})", options.Settings.Port, authority.Certificate is not null ? "on" : "off");
        Log.Information("  discovery   : udp/{Udp}, mDNS {Mdns}", options.Settings.DiscoveryPort, settings.Current.MdnsEnabled ? "on" : "off");
        Log.Information("  tls subject : {Subject}", authority.Subject ?? "(none)");
        Log.Information("  spki pin    : {Pin}", authority.SpkiPin ?? "(none)");
        Log.Information("  file roots  : {Roots}", string.Join(", ", sandbox.Roots.Select(r => r.CanonicalPath)));

        // A small landing page so the whole flow can be exercised from a browser on the
        // same machine without touching the phone.
        app.MapGet("/", () => Results.Redirect("/pairing"));

        await app.RunAsync().ConfigureAwait(false);
    }

    // =============================================================================

    private static void ApplyCommandLineOverrides(AgentOptions options, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--debug":
                    options.DebugMode = true;
                    options.Settings.Debug.Enabled = true;
                    options.Settings.Debug.VerboseLogging = true;
                    break;

                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                    options.Settings.Port = port;
                    i++;
                    break;

                case "--http" when i + 1 < args.Length && bool.TryParse(args[i + 1], out var http):
                    options.Settings.Security.TlsEnabled = !http;
                    i++;
                    break;

                case "--data-dir" when i + 1 < args.Length:
                    options.DataDirectory = args[++i];
                    break;

                case "--session-agent" when i + 1 < args.Length:
                    options.SessionAgentPath = args[++i];
                    break;

                case "--help" or "-h":
                    Console.WriteLine("""
                        ESI Remote PC - Windows Agent

                          --debug                  Verbose logging
                          --port <n>               HTTPS port (default 48220)
                          --http <true|false>      Disable TLS (signed requests are still required)
                          --data-dir <path>        Override the ProgramData location
                          --session-agent <path>   Path to ESI.RemotePc.Session.exe
                        """);
                    Environment.Exit(0);
                    break;
            }
        }
    }

    private static string ReadVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}

/// <summary>
/// Applies the agent's transport settings to Kestrel: limits, the HTTPS listener, and the
/// plaintext fallback when no certificate could be produced.
/// <para>
/// The listener binds every interface on purpose. "Local network only" is enforced by
/// <c>LocalNetworkMiddleware</c>, which keeps that policy in one reviewable place instead
/// of splitting it between the socket and the router.
/// </para>
/// </summary>
public sealed class KestrelTuning : IConfigureOptions<KestrelServerOptions>
{
    private readonly AgentOptions _options;
    private readonly int? _portOverride;

    public KestrelTuning(AgentOptions options, int? portOverride = null)
    {
        _options = options;
        _portOverride = portOverride;
    }

    /// <summary>The port the listener actually bound to, resolved once at configure time.</summary>
    public int Port => _portOverride ?? _options.Settings.Port;

    public void Configure(KestrelServerOptions kestrel)
    {
        var certificate = ResolveCertificate(kestrel);

        kestrel.Limits.MaxRequestBodySize = Math.Max(_options.Settings.Files.MaxUploadBytes, 1L);
        kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;

        // A little longer than the client's 20 s keepalive, so the server's own probe is
        // the thing that closes an idle socket rather than a client-side timeout.
        kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(65);
        kestrel.AddServerHeader = false;

        if (certificate is not null)
        {
            kestrel.ListenAnyIP(Port, listen =>
            {
                listen.UseHttps();
                listen.Protocols = HttpProtocols.Http1AndHttp2;
            });

            return;
        }

        Serilog.Log.Warning(
            "No TLS certificate is available; listening on plaintext HTTP. Request signing " +
            "still protects integrity and authenticity, but traffic is not confidential.");

        kestrel.ListenAnyIP(Port, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
    }

    /// <summary>
    /// Installs the HTTPS defaults and returns the certificate, or <c>null</c> when TLS is
    /// disabled or the certificate could not be produced.
    /// </summary>
    private System.Security.Cryptography.X509Certificates.X509Certificate2? ResolveCertificate(
        KestrelServerOptions kestrel)
    {
        if (!_options.Settings.Security.TlsEnabled)
        {
            Serilog.Log.Warning("TLS is disabled by configuration. Only request signing protects this channel.");
            return null;
        }

        // Deliberately its own CertificateAuthority instance: this object is the only
        // owner of the certificate, so nothing can dispose it out from under Kestrel.
        var certificate = new CertificateAuthority(
            new Serilog.Extensions.Logging.SerilogLoggerFactory(Serilog.Log.Logger)
                .CreateLogger<CertificateAuthority>(), _options).Certificate;

        if (certificate is null)
        {
            Serilog.Log.Error("The TLS certificate could not be created. Falling back to plaintext HTTP.");
            return null;
        }

        kestrel.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificate = certificate;

            // The phone authenticates itself with a signed request, not a client
            // certificate, so mutual TLS would add ceremony without adding security here.
            https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.NoCertificate;
        });

        return certificate;
    }
}

/// <summary>
/// Writes framework-generated error bodies (the automatic model-state 400) in the agent's
/// own error envelope, so a body-bind failure looks like every other error the phone
/// already knows how to read.
/// <para>
/// This is <c>IProblemDetailsService</c> rather than MVC's
/// <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c> because minimal APIs never
/// build an <c>ActionContext</c>: they call this service directly with a
/// <c>ProblemDetailsContext</c>. Registering the MVC factory instead compiles but silently
/// never runs, which is exactly the kind of bug that leaves a 400 with no body.
/// </para>
/// </summary>
public sealed class EsiProblemDetailsService(ILogger<EsiProblemDetailsService> log) : IProblemDetailsService
{
    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var httpContext = context.HttpContext;
        var problem = context.ProblemDetails;

        if (httpContext.Response.HasStarted)
            return ValueTask.CompletedTask;

        var details = problem switch
        {
            ValidationProblemDetails validation => validation.Errors
                .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value.FirstOrDefault() ?? "invalid"))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),

            _ when problem.Extensions.TryGetValue("errors", out var raw) && raw is IDictionary<string, string[]> errors =>
                errors.ToDictionary(pair => pair.Key, pair => pair.Value.FirstOrDefault() ?? "invalid", StringComparer.Ordinal),

            _ => null,
        };

        log.LogWarning("{Method} {Path} rejected: {Status} {Title} {Errors}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            problem.Status ?? httpContext.Response.StatusCode,
            problem.Title,
            details is null ? "(no field detail)" : string.Join("; ", details.Select(p => $"{p.Key}: {p.Value}")));

        ApiError.WriteAsync(httpContext, problem.Status ?? StatusCodes.Status400BadRequest,
                ErrorCodes.BadRequest,
                problem.Title ?? "The request could not be read.",
                details)
            .GetAwaiter().GetResult();

        return ValueTask.CompletedTask;
    }

    public ValueTask TryWriteAsync(ProblemDetailsContext context) => WriteAsync(context);
}

/// <summary>Wall-clock helper, injected so time-dependent code stays testable.</summary>
public sealed class AgentClock
{
    public long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public DateTimeOffset Now() => DateTimeOffset.UtcNow;
}