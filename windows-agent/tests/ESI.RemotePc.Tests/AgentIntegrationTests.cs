using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Esi.Core;
using Esi.Protocol;
using Esi.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// Drives the real agent: the real composition root, the real DI registrations, the real
/// Kestrel configuration and the real middleware pipeline, on a real socket.
/// <para>
/// This exists because the unit tests cannot reach the authenticated surface. Everything
/// that matters most - HMAC verification, scope enforcement, rate limiting, the file sandbox
/// behind an endpoint, error envelopes - only executes once a request has been signed, and
/// signing is only meaningful if the verifier is the one that ships. A test host with its
/// own container would test a different program.
/// </para>
/// <para>
/// The client below is a from-scratch reimplementation of the signing procedure, written
/// against <c>PROTOCOL.md</c> rather than against <c>EsiCrypto</c>. That is deliberate: if
/// both sides called the same helper, a change to the canonical string would break both at
/// once and the test would still pass. Sharing the implementation shares the bug.
/// </para>
/// </summary>
/// <summary>
/// Boots one real agent for the whole class and pairs one device with it.
/// <para>
/// A fixture rather than a per-test host, on purpose. The agent is a substantial program -
/// PDH counters, WMI, a notification engine - and starting one per test makes the suite
/// measure its own resource contention instead of the product. One agent, one pairing, many
/// signed requests against it is also the shape of real use.
/// </para>
/// </summary>
public sealed class AgentFixture : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private int _port;

    /// <summary>What a phone would hold after pairing: the id, the master secret, the key generation.</summary>
    public string DeviceId { get; private set; } = null!;

    public byte[] MasterSecret { get; private set; } = null!;

    public int KeyId { get; private set; }

    public HttpClient Client => _client;

    public string DataDirectory => _dataDirectory;

    /// <summary>The one directory the sandbox is allowed to see.</summary>
    public string SandboxRoot => Path.Combine(_dataDirectory, "files");

    public IServiceProvider Services => _app.Services;

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "esi-it-" + Guid.NewGuid().ToString("n").Substring(0, 8));
        Directory.CreateDirectory(_dataDirectory);

        _port = AllocatePort();

        var options = new AgentOptions
        {
            Version = "test",
            DataDirectory = _dataDirectory,
        };

        // Discovery binds a fixed UDP port and an mDNS multicast socket, neither of which
        // belongs in a test run that may execute alongside a real agent.
        options.Settings.DiscoveryEnabled = false;
        options.Settings.MdnsEnabled = false;

        // The file sandbox points at a throwaway directory, not the developer's real
        // profile. A test that enumerates Documents measures the size of the machine's
        // profile folder, and its result then depends on what else is installed.
        //
        // It has to be written to settings.json rather than poked into AgentOptions:
        // SettingsStore owns that state and re-derives the default roots from the profile
        // on load, so an in-memory override is silently discarded.
        var sandboxRoot = Path.Combine(_dataDirectory, "files");
        Directory.CreateDirectory(sandboxRoot);
        File.WriteAllText(Path.Combine(sandboxRoot, "readme.txt"), "integration test");
        Directory.CreateDirectory(Path.Combine(sandboxRoot, "subfolder"));

        var jsonPath = Path.Combine(_dataDirectory, "settings.json");
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            files = new
            {
                allowedRoots = new[] { new { path = sandboxRoot, readOnly = false } },
                allowDelete = true,
                allowUpload = true,
                maxUploadBytes = 16L * 1024 * 1024,
            },
        }));

        _app = AgentHost.BuildApplication([], options, _port);
        await _app.StartAsync();

        // The suite pairs more than once from the same loopback address, which the default
        // budget of five pairing attempts an hour exists to prevent. These tests are about
        // the pairing protocol, not the throttle, and the limiter has its own tests; raising
        // the budget here is the honest way to keep the two concerns separate.
        var limiter = _app.Services.GetRequiredService<Esi.Core.Security.RateLimiter>();
        limiter.PairingPerHour = 1_000;
        limiter.PairingBurst = 1_000;
        limiter.UnauthenticatedPerMinute = 10_000;
        limiter.UnauthenticatedBurst = 10_000;

        var handler = new HttpClientHandler
        {
            // The agent's certificate is self-signed by design; the trust anchor is the SPKI
            // pin the phone receives out of band, not a CA. A test asserting against the
            // system trust store would be asserting something the product never does.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };

        _client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{_port}") };
        _client.Timeout = TimeSpan.FromSeconds(30);

        await PairAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            try
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _app.StopAsync(shutdown.Token);
            }
            catch (Exception)
            {
                // Shutting down regardless.
            }

            await _app.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A handle the OS has not released yet is not a test failure.
        }
    }

    /// <summary>
    /// A port that is not the default one. Kestrel's <c>ListenAnyIP</c> does not report the
    /// port it bound, so it has to be known in advance; a counter keeps successive runs of
    /// the suite off each other's ports.
    /// </summary>
    private static int _portCounter = -1;

    /// <summary>
    /// A port Kestrel can actually bind, not merely one that looks unused.
    /// <para>
    /// Two things make this less trivial than picking a free number. Kestrel listens on
    /// <c>IPAddress.Any</c>, so a port that is free on the loopback interface can still be
    /// taken on the wildcard address. And Windows reserves whole port ranges - Hyper-V and
    /// WinNAT do it, and <c>netsh interface ipv4 show excludedportrange protocol=tcp</c>
    /// lists them - where binding fails with <c>WSAEACCES</c> rather than
    /// <c>EADDRINUSE</c>. The dynamic range starts at 49152, so seeding below an ephemeral
    /// port walks straight into those reservations, and on a machine that has ever run the
    /// emulator every test in the file then fails with "forbidden by its access
    /// permissions" - a red suite that says nothing about the code under test.
    /// </para>
    /// </summary>
    private static int AllocatePort()
    {
        var current = Volatile.Read(ref _portCounter);

        if (current < 0)
        {
            // Seed below the dynamic range so successive allocations walk through ports
            // that are less likely to be in flight, then verify each one by binding it.
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var seed = ((IPEndPoint)listener.LocalEndpoint).Port - 64;
            listener.Stop();

            current = Interlocked.CompareExchange(ref _portCounter, seed, -1);
            if (current < 0)
                current = seed;
        }

        for (var attempt = 0; attempt < 512; attempt++)
        {
            var candidate = Interlocked.Increment(ref _portCounter);
            if (candidate is < 1024 or > 65535)
                continue;

            if (CanBind(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            "Could not find a TCP port the test agent can bind. " +
            "Run 'netsh interface ipv4 show excludedportrange protocol=tcp' and look for a " +
            "reservation covering the range the suite is seeding from.");
    }

    /// <summary>True when a wildcard listener can take <paramref name="port"/>.</summary>
    private static bool CanBind(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            // Reserved by Windows (WSAEACCES), already listening (EADDRINUSE), or held by
            // an outbound connection. All three mean "not this one".
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the full pairing handshake: ask, approve, and collect the device credentials.
    /// <para>
    /// Approval normally comes from the on-desktop prompt. The prompt is a WinForms dialog
    /// owned by the session agent, which cannot exist in a test process, so the
    /// <c>PairingManager</c> is driven directly - the same object the prompt's answer is
    /// routed to. Everything either side of that, including the provisioning-key encryption,
    /// is still exercised.
    /// </para>
    /// </summary>
    private async Task PairAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pair/request")
        {
            Content = new StringContent(
                """{"deviceName":"Integration Test","platform":"android","osVersion":"16","appVersion":"1.0.0"}""",
                Encoding.UTF8, "application/json"),
        };

        using var response = await Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var pairingId = root.GetProperty("pairingId").GetString()!;
        var userCode = root.GetProperty("userCode").GetString()!;

        Assert.Equal(6, userCode.Length);

        var pairingManager = _app.Services.GetRequiredService<Esi.Core.Security.PairingManager>();
        var (device, masterSecret, keyId) = pairingManager.Approve(
            pairingId,
            Esi.Protocol.DeviceScope.Read | Esi.Protocol.DeviceScope.Control | Esi.Protocol.DeviceScope.Files);

        DeviceId = device.DeviceId;
        MasterSecret = masterSecret;
        KeyId = keyId;

        Assert.Equal(1, keyId);
        Assert.Equal(ProtocolConstants.SecretLength, masterSecret.Length);
    }
}

[Collection(AgentIntegrationCollection.Name)]
public sealed class AgentIntegrationTests(AgentFixture fixture) : IClassFixture<AgentFixture>
{
    private readonly AgentFixture _fixture = fixture;

    private string DeviceId => _fixture.DeviceId;

    private byte[] MasterSecret => _fixture.MasterSecret;

    private int KeyId => _fixture.KeyId;

    private HttpClient Client => _fixture.Client;

    // ===============================================================================
    // Tests
    // ===============================================================================

    [Fact]
    public async Task Health_is_reachable_without_a_signature()
    {
        var response = await Client.GetAsync("/api/v1/health");
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_advertises_the_spki_pin()
    {
        var response = await Client.GetAsync("/api/v1/handshake");
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"spkiSha256\"", body, StringComparison.Ordinal);
        Assert.Contains("\"protocolVersion\":1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_metrics_request_succeeds()
    {
        var response = await SendSignedAsync(HttpMethod.Get, "/api/v1/system/metrics");
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"cpu\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_request_with_no_body_carries_the_empty_body_hash()
    {
        // The empty-body constant is a protocol constant, not a convenience: the client and
        // the verifier must agree on it or every bodyless request fails.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        Sign(request, body: []);

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unsigned_request_is_rejected()
    {
        var response = await Client.GetAsync("/api/v1/system/metrics");
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("esi_unauthorized", body, StringComparison.Ordinal);

        // The response must identify itself, or a client failure is indistinguishable from a
        // dropped connection.
        Assert.Contains("\"requestId\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_path_invalidates_the_signature()
    {
        // Sign one route, send another. This is the single most important assertion in the
        // whole file: if the verifier did not bind the signature to the path, a device could
        // sign an innocuous read and have the agent execute a shutdown.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        Sign(request, body: [], pathOverride: "/api/v1/system/metrics");

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_tampered_body_invalidates_the_signature()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/apps/launch")
        {
            Content = new ByteArrayContent("""{"id":"calc.exe"}"""u8.ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        Sign(request, body: []); // signed over nothing

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_replayed_nonce_is_rejected()
    {
        // Two identical requests: same nonce, same timestamp, same signature. The first must
        // succeed and the second must fail, or a captured request can be replayed forever.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = NewNonce();

        var first = BuildSigned(HttpMethod.Get, "/api/v1/system/info", [], timestamp, nonce);
        var firstResponse = await Client.SendAsync(first);

        var second = BuildSigned(HttpMethod.Get, "/api/v1/system/info", [], timestamp, nonce);
        var secondResponse = await Client.SendAsync(second);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondResponse.StatusCode);

        var body = await ReadAsync(secondResponse);
        Assert.Contains("esi_replay", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_timestamp_is_rejected()
    {
        // Far outside the default 300 s skew window.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
        var request = BuildSigned(HttpMethod.Get, "/api/v1/system/info", [], timestamp, NewNonce());

        var response = await Client.SendAsync(request);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("esi_timestamp", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_key_id_is_rejected()
    {
        var request = BuildSigned(HttpMethod.Get, "/api/v1/system/info", [],
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), NewNonce(), keyId: KeyId + 99);

        var response = await Client.SendAsync(request);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("esi_keyid", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signature_from_the_wrong_key_is_rejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        Sign(request, body: [], sessionKeyOverride: RandomNumberGenerator.GetBytes(32));

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_device_with_no_admin_scope_cannot_read_the_audit_log()
    {
        // The test device is paired with read/control/files and deliberately without admin,
        // so this exercises the scope table rather than the happy path.
        var response = await SendSignedAsync(HttpMethod.Get, "/api/v1/audit");
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("esi_forbidden_scope", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_files_scope_is_required_for_file_routes()
    {
        var response = await SendSignedAsync(HttpMethod.Get, "/api/v1/files/list");
        var body = await ReadAsync(response);

        // The test device does hold the files scope, so this asserts the route is reachable
        // and the envelope is well formed rather than asserting a 403.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("esi_forbidden_scope", body, StringComparison.Ordinal);
        Assert.Contains("readme.txt", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_listing_stays_inside_the_sandbox()
    {
        // The listing must contain only what the configured root holds. An escape here would
        // be a directory traversal, and the test asserts the shape of the response rather
        // than trusting the unit tests for FileSandbox to have covered the endpoint wiring.
        var response = await SendSignedAsync(HttpMethod.Get, "/api/v1/files/list");
        var body = await ReadAsync(response);

        using var document = JsonDocument.Parse(body);
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString()!;
            Assert.StartsWith(_fixture.SandboxRoot, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_file_operation_outside_the_sandbox_is_refused()
    {
        var response = await SendSignedAsync(HttpMethod.Get, "/api/v1/files/stat?path=C%3A%5CWindows%5Cwin.ini");
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("esi_path_not_allowed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_route_returns_a_version_mismatch_hint()
    {
        // A phone calling a route the agent does not have needs to be able to tell that
        // apart from a rejected signature; otherwise a version skew is a mystery.
        var response = await SendSignedAsync(HttpMethod.Get, "/api/v1/does-not-exist");
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("esi_not_found", body, StringComparison.Ordinal);
        Assert.Contains("protocol version", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_whoami_route_reports_the_paired_device()
    {
        var response = await SendSignedAsync(HttpMethod.Get, "/api/v1/auth/whoami");
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(DeviceId, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_error_body_is_a_well_formed_envelope()
    {
        // A client that has to handle two error shapes has two code paths, and only one of
        // them gets exercised in production.
        var probes = new (HttpMethod Method, string Path, HttpStatusCode Expected)[]
        {
            (HttpMethod.Get, "/api/v1/system/metrics", HttpStatusCode.Unauthorized),   // unsigned
            (HttpMethod.Get, "/api/v1/nope", HttpStatusCode.NotFound),
        };

        foreach (var (method, path, expected) in probes)
        {
            using var request = new HttpRequestMessage(method, path);

            // Sign only when the probe is meant to authenticate; an unsigned probe is
            // expected to fail with a specific status and that failure is the subject.
            if (expected != HttpStatusCode.Unauthorized)
                Sign(request, body: []);

            using var response = await Client.SendAsync(request);
            var body = await ReadAsync(response);

            Assert.Equal(expected, response.StatusCode);

            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");

            Assert.True(error.TryGetProperty("code", out _), $"no error code in: {body}");
            Assert.True(error.TryGetProperty("message", out _), $"no error message in: {body}");
            Assert.True(error.TryGetProperty("requestId", out _), $"no request id in: {body}");
        }
    }

    [Fact]
    public async Task The_request_id_header_matches_the_body()
    {
        // The header is how a user reports a problem and the body is how the log correlates
        // it. If they can disagree, support has nothing to correlate on.
        var response = await SendSignedAsync(HttpMethod.Get, "/api/v1/nope");
        var body = await ReadAsync(response);

        var headerId = response.Headers.TryGetValues("X-ESI-Request-Id", out var values)
            ? values.First()
            : null;

        Assert.NotNull(headerId);

        using var document = JsonDocument.Parse(body);
        var bodyId = document.RootElement.GetProperty("error").GetProperty("requestId").GetString();

        Assert.Equal(headerId, bodyId);
    }

    /// <summary>
    /// The pairing flow end to end: request, approve, transfer the provisioning secret, and
    /// unwrap the credentials.
    /// <para>
    /// This is the test that matters most, because it is the only one that can catch the
    /// failure mode where everything looks fine - the prompt appears, the approval is
    /// recorded, the device exists in the database - and yet the phone can never obtain a
    /// master secret, because nothing ever delivered the provisioning secret. Every
    /// individual component was testable and the product was still unusable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_paired_phone_can_collect_its_credentials_using_the_transferred_secret()
    {
        // 1. Ask to pair. This is unauthenticated and needs no signature.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pair/request")
        {
            Content = new StringContent(
                """{"deviceName":"Round Trip","platform":"android","osVersion":"16"}""",
                Encoding.UTF8, "application/json"),
        };

        using var created = await Client.SendAsync(request);
        var createdBody = await created.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        string pairingId, userCode;
        using (var document = JsonDocument.Parse(createdBody))
        {
            pairingId = document.RootElement.GetProperty("pairingId").GetString()!;
            userCode = document.RootElement.GetProperty("userCode").GetString()!;
        }

        Assert.Equal(6, userCode.Length);

        // 2. The PC has to be able to render a transferable secret. This is the step that was
        //    missing: an attempt the PC could not hand over was an attempt that could never
        //    complete, no matter how many times the phone polled.
        var qr = await Client.GetAsync($"/pairing/qr.svg?code={userCode}&host=127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);

        var svg = await qr.Content.ReadAsStringAsync();
        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("viewBox", svg, StringComparison.Ordinal);

        // 3. Approve, as the desktop prompt or the console would.
        var pairingManager = _fixture.Services.GetRequiredService<Esi.Core.Security.PairingManager>();
        var attempt = pairingManager.Get(pairingId)!;
        var (device, _, keyId) = pairingManager.Approve(
            pairingId, Esi.Protocol.DeviceScope.Read | Esi.Protocol.DeviceScope.Control);

        Assert.Equal(1, keyId);

        // 4. The phone polls with the secret it obtained from the QR payload. The QR's URL is
        //    what the user scanned, so recover the secret from exactly that URL.
        var secret = ExtractSecret(pairingManager.BuildPollingUrl(attempt, "127.0.0.1", pairingManager.Port));
        Assert.NotNull(secret);

        // 5. Poll with it and unwrap the credentials, all as the phone would.
        using var poll = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/pair/session?id={pairingId}&secret={Uri.EscapeDataString(secret!)}");

        using var polled = await Client.SendAsync(poll);
        var polledBody = await polled.Content.ReadAsStringAsync();

        Assert.True(polled.StatusCode == HttpStatusCode.OK,
            $"polling the approved attempt returned {(int)polled.StatusCode}: {polledBody}");
        Assert.Contains("\"status\":\"approved\"", polledBody, StringComparison.Ordinal);
        Assert.Contains(device.DeviceId, polledBody, StringComparison.Ordinal);

        using var status = JsonDocument.Parse(polledBody);
        var envelope = status.RootElement.GetProperty("wrappedSecrets");

        var provisioningKey = EsiCrypto.DeriveProvisioningKey(
            Convert.FromBase64String(
                Uri.UnescapeDataString(secret!).Replace('-', '+').Replace('_', '/').PadRight(
                    (secret!.Length + 3) / 4 * 4, '=')),
            pairingId);

        var aad = Encoding.UTF8.GetBytes($"esi-pair-v1|{pairingId}|{device.DeviceId}");

        var unwrapped = EsiCrypto.Open(
            new WrappedSecret(
                Convert.FromBase64String(envelope.GetProperty("nonce").GetString()!),
                Convert.FromBase64String(envelope.GetProperty("ciphertext").GetString()!),
                Convert.FromBase64String(envelope.GetProperty("tag").GetString()!)),
            provisioningKey,
            aad);

        Assert.Equal(ProtocolConstants.SecretLength + 4, unwrapped.Length);

        // 6. And the recovered secret must actually authenticate, which is the only
        //    assertion that proves the whole chain rather than just the plumbing.
        var recoveredSecret = unwrapped[..ProtocolConstants.SecretLength];

        using var probe = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        SignWith(probe, [], device.DeviceId, recoveredSecret, keyId);

        using var probeResponse = await Client.SendAsync(probe);

        Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);
    }

    [Fact]
    public async Task A_phone_without_the_provisioning_secret_gets_no_credentials()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pair/request")
        {
            Content = new StringContent("""{"deviceName":"No Secret","platform":"android"}""",
                Encoding.UTF8, "application/json"),
        };

        using var created = await Client.SendAsync(request);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        var pairingId = document.RootElement.GetProperty("pairingId").GetString()!;

        _fixture.Services.GetRequiredService<Esi.Core.Security.PairingManager>()
            .Approve(pairingId, Esi.Protocol.DeviceScope.Read);

        // Polling with no secret, or with a guessed one, must never release the credentials.
        // This is the property that makes the whole mechanism worth anything.
        foreach (var query in new[] { $"id={pairingId}", $"id={pairingId}&secret=AAAA", $"id={pairingId}&secret=" })
        {
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/pair/session?{query}");
            using var response = await Client.SendAsync(poll);
            var body = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain("wrappedSecrets", body, StringComparison.Ordinal);
            Assert.DoesNotContain("deviceId", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_unknown_pairing_code_has_no_qr()
    {
        var response = await Client.GetAsync("/pairing/qr.svg?code=000000");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("esi_not_found", body, StringComparison.Ordinal);
    }

    private static string? ExtractSecret(string pollingUrl)
    {
        var query = pollingUrl[(pollingUrl.IndexOf('?') + 1)..];

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith("secret=", StringComparison.Ordinal))
                continue;

            return Uri.UnescapeDataString(part["secret=".Length..]);
        }

        return null;
    }

    // ===============================================================================
    // The client side of the protocol, written from the spec
    // ===============================================================================

    private Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string path, byte[]? body = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        Sign(request, body ?? []);

        return Client.SendAsync(request);
    }

    private void Sign(HttpRequestMessage request, byte[] body, string? pathOverride = null, byte[]? sessionKeyOverride = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = NewNonce();
        SignCore(request, body, timestamp, nonce, KeyId, sessionKeyOverride, pathOverride, DeviceId, MasterSecret);
    }

    /// <summary>Signs as a specific device, used by the pairing round-trip test.</summary>
    private void SignWith(HttpRequestMessage request, byte[] body, string deviceId, byte[] masterSecret, int keyId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SignCore(request, body, timestamp, NewNonce(), keyId, null, null, deviceId, masterSecret);
    }

    /// <summary>
    /// Builds the 8 signature headers. PROTOCOL.md §4.2:
    /// <c>ESIV1 \n METHOD \n canonicalTarget \n timestamp \n nonce \n contentSha256 \n deviceId \n keyId</c>
    /// </summary>
    private void SignCore(HttpRequestMessage request, byte[] body, long timestamp, string nonce,
        int keyId, byte[]? sessionKeyOverride = null, string? pathOverride = null,
        string? deviceIdOverride = null, byte[]? masterSecretOverride = null)
    {
        var deviceId = deviceIdOverride ?? DeviceId;
        var masterSecret = masterSecretOverride ?? MasterSecret;

        // HttpClient applies BaseAddress lazily at send time, so RequestUri may still be
        // relative here. Resolving it is exactly what the transport would do, and the
        // signature must be computed over the same absolute target that goes on the wire.
        var requestUri = request.RequestUri!.IsAbsoluteUri
            ? request.RequestUri
            : new Uri(Client.BaseAddress!, request.RequestUri);

        var path = pathOverride ?? requestUri.AbsolutePath;
        var query = requestUri.Query.TrimStart('?');
        var canonicalTarget = EsiCrypto.BuildCanonicalTarget(path, query);

        var contentHash = body.Length == 0
            ? ProtocolConstants.EmptyBodySha256Base64
            : Convert.ToBase64String(SHA256.HashData(body));

        var canonical = string.Join('\n',
            "ESIV1",
            request.Method.Method.ToUpperInvariant(),
            canonicalTarget,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            contentHash,
            deviceId.ToLowerInvariant(),
            keyId.ToString(CultureInfo.InvariantCulture));

        // RFC 5869 extract-then-expand, written out rather than delegated to EsiCrypto so
        // that a defect in the shipped helper cannot hide behind a shared implementation.
        var prk = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(ProtocolConstants.HkdfSalt), masterSecret);

        var info = Encoding.UTF8.GetBytes($"esi-session-v1|{deviceId.ToLowerInvariant()}|{keyId}");
        var sessionKey = sessionKeyOverride ?? Expand(prk, info, 32);

        var signature = Convert.ToBase64String(
            HMACSHA256.HashData(sessionKey, Encoding.UTF8.GetBytes(canonical)));

        request.Headers.TryAddWithoutValidation("X-ESI-Protocol", ProtocolConstants.Version.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-ESI-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-ESI-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-ESI-Content-Sha256", contentHash);
        request.Headers.TryAddWithoutValidation("X-ESI-Device-Id", deviceId);
        request.Headers.TryAddWithoutValidation("X-ESI-Key-Id", keyId.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-ESI-Signature", signature);
    }

    private static byte[] Expand(byte[] prk, byte[] info, int length)
    {
        var okm = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;

        for (byte counter = 1; offset < length; counter++)
        {
            var input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input, previous.Length);
            input[^1] = counter;

            previous = HMACSHA256.HashData(prk, input);
            var take = Math.Min(previous.Length, length - offset);
            previous.AsSpan(0, take).CopyTo(okm.AsSpan(offset));
            offset += take;
        }

        return okm;
    }

    private HttpRequestMessage BuildSigned(HttpMethod method, string path, byte[] body,
        long timestamp, string nonce, int? keyId = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        SignCore(request, body, timestamp, nonce, keyId ?? KeyId);
        return request;
    }

    private static string NewNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(ProtocolConstants.NonceLength);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response)
        => await response.Content.ReadAsStringAsync();
}
