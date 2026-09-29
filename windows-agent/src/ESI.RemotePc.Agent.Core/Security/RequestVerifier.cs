using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Esi.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Esi.Core.Security;

/// <summary>Result of verifying a signed request.</summary>
public sealed record SignatureVerification(
    bool Succeeded,
    string ErrorCode,
    string? ErrorMessage,
    PairedDevice? Device = null,
    int KeyId = 0)
{
    public static SignatureVerification Fail(string code, string message) => new(false, code, message);
    public static SignatureVerification Ok(PairedDevice device, int keyId) => new(true, "", null, device, keyId);
}

/// <summary>Parsed signature headers from a request.</summary>
public sealed record SignatureHeaders(string DeviceId, int KeyId, long Timestamp, string Nonce, string ContentSha256, string Signature)
{
    public static bool TryParse(IHeaderDictionaryLike headers, out SignatureHeaders? parsed, out string? error)
    {
        parsed = null;
        error = null;

        string? deviceId = headers.Get("X-ESI-Device-Id");
        string? keyIdRaw = headers.Get("X-ESI-Key-Id");
        string? timestampRaw = headers.Get("X-ESI-Timestamp");
        string? nonce = headers.Get("X-ESI-Nonce");
        string? content = headers.Get("X-ESI-Content-Sha256");
        string? signature = headers.Get("X-ESI-Signature");

        if (string.IsNullOrWhiteSpace(deviceId) ||
            string.IsNullOrWhiteSpace(keyIdRaw) ||
            string.IsNullOrWhiteSpace(timestampRaw) ||
            string.IsNullOrWhiteSpace(nonce) ||
            string.IsNullOrWhiteSpace(content) ||
            string.IsNullOrWhiteSpace(signature))
        {
            error = "The request is missing one or more X-ESI-* signature headers.";
            return false;
        }

        if (!int.TryParse(keyIdRaw, out var keyId) || keyId < 1)
        {
            error = "X-ESI-Key-Id is not a valid key generation.";
            return false;
        }

        if (!long.TryParse(timestampRaw, out var timestamp))
        {
            error = "X-ESI-Timestamp is not a Unix timestamp.";
            return false;
        }

        if (nonce.Length is < 16 or > 64)
        {
            error = "X-ESI-Nonce has an implausible length.";
            return false;
        }

        if (signature.Length is < 20 or > 128)
        {
            error = "X-ESI-Signature has an implausible length.";
            return false;
        }

        parsed = new SignatureHeaders(deviceId.Trim().ToLowerInvariant(), keyId, timestamp, nonce, content, signature);
        return true;
    }
}

/// <summary>Header accessor abstraction so the verifier can be unit tested without HTTP.</summary>
public interface IHeaderDictionaryLike
{
    string? Get(string name);
}

/// <summary>
/// Verifies HMAC-signed requests and enforces replay protection.
/// <para>
/// The verification order in <see cref="Verify"/> is deliberate and is the order in
/// <c>docs/PROTOCOL.md</c> §4.3. Cheapest and most selective checks come first, so a
/// flood of bad requests costs as little as possible:
/// </para>
/// <list type="number">
///   <item><description>Header shape and length bounds — no I/O at all.</description></item>
///   <item><description>Device lookup — one indexed read.</description></item>
///   <item><description>Timestamp window — rejects anything captured more than five minutes ago.</description></item>
///   <item><description>Nonce replay — a per-device in-memory set, which is what makes a
///   captured request unusable even inside the window.</description></item>
///   <item><description>Key generation — current, or previous inside the rotation grace period.</description></item>
///   <item><description>Signature, compared in constant time.</description></item>
///   <item><description>Body hash, also constant time.</description></item>
/// </list>
/// <para>
/// The nonce cache is in memory rather than in SQLite on purpose: it only needs to outlive
/// the replay window, and putting a write on every authenticated request would make the
/// metrics path contend on the database for no benefit.
/// </para>
/// </summary>
public sealed class RequestVerifier(DeviceStore devices)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _nonces = new(StringComparer.Ordinal);
    private DateTime _lastNonceSweep = DateTime.UtcNow;

    public SignatureVerification Verify(
        IHeaderDictionaryLike headers,
        string method,
        string rawPath,
        string? rawQuery,
        ReadOnlySpan<byte> body,
        int clockSkewSeconds)
    {
        if (!SignatureHeaders.TryParse(headers, out var parsed, out var headerError))
            return SignatureVerification.Fail(ErrorCodes.Unauthorized, headerError!);

        var request = parsed!;

        var device = devices.Get(request.DeviceId);
        if (device is null)
            return SignatureVerification.Fail(ErrorCodes.Unauthorized, "The device id is not paired with this agent.");

        if (device.MasterSecret.Length != ProtocolConstants.SecretLength)
        {
            return SignatureVerification.Fail(ErrorCodes.Unauthorized,
                "The stored secret for this device is unusable. The device must be re-paired.");
        }

        if (!device.Enabled)
            return SignatureVerification.Fail(ErrorCodes.ForbiddenScope, "This device has been disabled on the PC.");

        // ---- 3. Timestamp window ------------------------------------------------
        var now = AgentDatabase.NowUnix();
        var skew = Math.Abs(now - request.Timestamp);
        if (skew > clockSkewSeconds)
        {
            return SignatureVerification.Fail(ErrorCodes.Timestamp,
                $"The request timestamp is {skew}s away from the agent's clock, outside the {clockSkewSeconds}s window. " +
                "Check the clock on both devices.");
        }

        // ---- 4. Nonce replay ----------------------------------------------------
        if (!TryRecordNonce(request.DeviceId, request.Nonce, now, clockSkewSeconds))
            return SignatureVerification.Fail(ErrorCodes.Replay, "This nonce has already been used.");

        // ---- 5. Key generation --------------------------------------------------
        var resolvedKeyId = request.KeyId;
        byte[]? masterSecret = null;

        if (request.KeyId == device.KeyId)
        {
            masterSecret = device.MasterSecret;
        }
        else if (device.PreviousKeyId == request.KeyId &&
                 device.PreviousKeyValidUntil is { } until &&
                 now <= until)
        {
            // The previous generation is still honoured during the rotation grace period
            // so requests already in flight do not fail.
            masterSecret = device.MasterSecret;
        }
        else
        {
            return SignatureVerification.Fail(ErrorCodes.KeyId,
                $"Key generation {request.KeyId} is not valid for this device (current is {device.KeyId}).");
        }

        // ---- 6. Signature -------------------------------------------------------
        var canonicalTarget = EsiCrypto.BuildCanonicalTarget(rawPath, rawQuery);
        var canonical = EsiCrypto.BuildCanonicalString(
            method,
            canonicalTarget,
            request.Timestamp,
            request.Nonce,
            request.ContentSha256,
            request.DeviceId,
            request.KeyId);

        var sessionKey = EsiCrypto.DeriveSessionKey(masterSecret!, request.DeviceId, request.KeyId);
        var expected = EsiCrypto.Sign(sessionKey, canonical);

        if (!EsiCrypto.SignatureEquals(expected, request.Signature))
            return SignatureVerification.Fail(ErrorCodes.Signature, "The request signature is not valid.");

        // ---- 7. Body hash -------------------------------------------------------
        // Checked after the signature so a mismatched body cannot be used to probe the
        // HMAC, and compared in constant time.
        var actualContentHash = EsiCrypto.HashContent(body);
        if (!EsiCrypto.SignatureEquals(actualContentHash, request.ContentSha256))
            return SignatureVerification.Fail(ErrorCodes.Signature, "The request body does not match X-ESI-Content-Sha256.");

        return SignatureVerification.Ok(device, resolvedKeyId);
    }

    /// <summary>
    /// Accepts a nonce only if it has not been seen for this device inside the replay
    /// window. The cache is swept periodically so a long-lived agent does not accumulate
    /// nonces forever.
    /// </summary>
    private bool TryRecordNonce(string deviceId, string nonce, long nowUnix, int windowSeconds)
    {
        var deviceNonces = _nonces.GetOrAdd(deviceId, _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal));

        // A nonce is remembered for twice the window, so a request that is replayed after
        // the window has already been rejected by the timestamp check anyway.
        if (!deviceNonces.TryAdd(nonce, nowUnix))
            return false;

        SweepNonces(nowUnix, windowSeconds);
        return true;
    }

    private void SweepNonces(long nowUnix, int windowSeconds)
    {
        if ((DateTime.UtcNow - _lastNonceSweep).TotalSeconds < 30)
            return;

        _lastNonceSweep = DateTime.UtcNow;
        var cutoff = nowUnix - (windowSeconds * 2);

        foreach (var (deviceId, nonces) in _nonces)
        {
            foreach (var (nonce, seenAt) in nonces)
            {
                if (seenAt < cutoff)
                    nonces.TryRemove(nonce, out _);
            }

            if (nonces.IsEmpty)
                _nonces.TryRemove(deviceId, out _);
        }
    }

    /// <summary>Clears the nonce cache. Called on rotation so in-flight retries still work.</summary>
    public void ResetNonces() => _nonces.Clear();

    /// <summary>Diagnostics: how many nonces are currently tracked.</summary>
    public int TrackedNonceCount => _nonces.Sum(kv => kv.Value.Count);
}
