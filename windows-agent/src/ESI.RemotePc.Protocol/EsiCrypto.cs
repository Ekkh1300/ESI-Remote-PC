using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Esi.Protocol;

/// <summary>
/// HMAC-SHA256 request signing, key derivation and secret wrapping.
/// <para>
/// This is the single most security-critical file in the project. The canonical string
/// layout and every derivation label here MUST match the mobile client byte for byte,
/// and MUST match <c>docs/PROTOCOL.md</c> §4 and §5.
/// </para>
/// </summary>
public static class EsiCrypto
{
    // ---------------------------------------------------------------- HKDF (RFC 5869)

    /// <summary>HKDF-SHA256 extract step. Note: the salt is the HMAC <em>key</em>, the IKM is the input.</summary>
    public static byte[] HkdfExtract(byte[] salt, byte[] ikm)
    {
        return HMACSHA256.HashData(salt, ikm);
    }

    /// <summary>HKDF-SHA256 expand step.</summary>
    /// <param name="prk">Output of <see cref="HkdfExtract"/>.</param>
    /// <param name="info">Context/application specific info string.</param>
    /// <param name="length">Output length in bytes (max 255 * 32).</param>
    public static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        if (length is < 1 or > (255 * 32))
            throw new ArgumentOutOfRangeException(nameof(length), length, "HKDF output length must be 1..8160.");

        var infoBytes = info ?? [];
        var okm = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;

        for (byte counter = 1; offset < length; counter++)
        {
            // T(i) = HMAC(PRK, T(i-1) | info | i)
            var input = new byte[previous.Length + infoBytes.Length + 1];
            Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
            Buffer.BlockCopy(infoBytes, 0, input, previous.Length, infoBytes.Length);
            input[^1] = counter;

            previous = HMACSHA256.HashData(prk, input);
            var take = Math.Min(previous.Length, length - offset);
            Buffer.BlockCopy(previous, 0, okm, offset, take);
            offset += take;
        }

        return okm;
    }

    /// <summary>Full HKDF-SHA256 (extract then expand).</summary>
    public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int length)
    {
        var prk = HkdfExtract(salt, ikm);
        return HkdfExpand(prk, info, length);
    }

    /// <summary>Convenience wrapper that derives a key from the v1 protocol salt.</summary>
    public static byte[] DeriveKey(byte[] masterSecret, string label, int length = ProtocolConstants.SecretLength)
    {
        return Hkdf(
            masterSecret,
            Encoding.UTF8.GetBytes(ProtocolConstants.HkdfSalt),
            Encoding.UTF8.GetBytes(label),
            length);
    }

    // ------------------------------------------------------- Protocol key derivation

    /// <summary>Key that authenticates this device's requests.</summary>
    public static byte[] DeriveSessionKey(byte[] masterSecret, string deviceId, int keyId)
        => DeriveKey(masterSecret, $"esi-session-v1|{Normalise(deviceId)}|{keyId}");

    /// <summary>Key used to wrap a rotated master secret for delivery to the device.</summary>
    public static byte[] DeriveWrapKey(byte[] masterSecret, string deviceId)
        => DeriveKey(masterSecret, $"esi-wrap-v1|{Normalise(deviceId)}");

    /// <summary>Key chained into the audit log so tampering is detectable.</summary>
    public static byte[] DeriveAuditKey(byte[] masterSecret, string deviceId, int keyId)
        => DeriveKey(masterSecret, $"esi-audit-v1|{Normalise(deviceId)}|{keyId}");

    /// <summary>
    /// The one canonical form of a device id.
    /// <para>
    /// The canonical string lowercases the device id, and the device table looks it up
    /// lowercased, so a derivation that did not would be the one place in the protocol where
    /// the same identifier had two spellings. That is exactly the kind of asymmetry that
    /// produces a device which authenticates on one hop and fails on the next, for reasons
    /// nobody can reproduce.
    /// </para>
    /// </summary>
    private static string Normalise(string deviceId) => deviceId.ToLowerInvariant();

    /// <summary>One-time key that encrypts the initial secret delivery for a pairing attempt.</summary>
    public static byte[] DeriveProvisioningKey(byte[] provisioningSecret, string pairingId)
        => DeriveKey(provisioningSecret, $"esi-provision-v1|{pairingId}");

    // ------------------------------------------------------------- AES-256-GCM wrapping

    /// <summary>
    /// Encrypts a short secret payload with AES-256-GCM. The 12-byte nonce is random per
    /// call and prepended to the ciphertext so callers cannot accidentally reuse one.
    /// </summary>
    public static WrappedSecret Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        return new WrappedSecret(nonce, ciphertext, tag);
    }

    /// <summary>Reverses <see cref="Seal"/>. Throws <see cref="CryptographicException"/> on tamper.</summary>
    public static byte[] Open(WrappedSecret wrapped, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData)
    {
        var plaintext = new byte[wrapped.Ciphertext.Length];
        using var aes = new AesGcm(key, wrapped.Tag.Length);
        aes.Decrypt(
            wrapped.Nonce,
            wrapped.Ciphertext,
            wrapped.Tag,
            plaintext,
            associatedData);
        return plaintext;
    }

    // ------------------------------------------------------------- Request signatures

    /// <summary>
    /// Builds the 8-line canonical string that gets HMAC'd. See docs/PROTOCOL.md §4.2.
    /// Order is fixed and the lines are LF-separated with no trailing newline.
    /// </summary>
    public static string BuildCanonicalString(
        string method,
        string canonicalTarget,
        long timestamp,
        string nonce,
        string contentSha256Base64,
        string deviceId,
        int keyId)
    {
        return string.Join('\n',
            ProtocolConstants.SignatureDomain,
            method.ToUpperInvariant(),
            canonicalTarget,
            timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            nonce,
            contentSha256Base64,
            deviceId.ToLowerInvariant(),
            keyId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Normalises a request target into its canonical form: raw path + sorted raw query.</summary>
    public static string BuildCanonicalTarget(string rawPath, string? rawQuery)
    {
        // Do NOT decode or re-encode: both sides must agree on the exact bytes on the wire.
        var path = string.IsNullOrEmpty(rawPath) ? "/" : rawPath;

        if (string.IsNullOrEmpty(rawQuery))
            return path;

        var trimmed = rawQuery[0] == '?' ? rawQuery[1..] : rawQuery;
        if (trimmed.Length == 0)
            return path;

        var segments = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        // Ordinal sort over the whole "k=v" segment so duplicate keys order deterministically
        // and without needing any canonicalisation of the components.
        Array.Sort(segments, StringComparer.Ordinal);
        return path + "?" + string.Join('&', segments);
    }

    /// <summary>Computes the request signature.</summary>
    public static string Sign(ReadOnlySpan<byte> sessionKey, string canonicalString)
    {
        var mac = HMACSHA256.HashData(sessionKey, Encoding.UTF8.GetBytes(canonicalString));
        return Convert.ToBase64String(mac);
    }

    /// <summary>Constant-time signature comparison. Never use <c>==</c> on secrets or signatures.</summary>
    public static bool SignatureEquals(string expectedBase64, string actualBase64)
    {
        if (!TryDecodeBase64(expectedBase64, out var a) || !TryDecodeBase64(actualBase64, out var b))
            return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Constant-time comparison of two raw secrets. <c>FixedTimeEquals</c> already
    /// short-circuits on length, so a length mismatch is not a meaningful secret here.
    /// </summary>
    public static bool SecretEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => CryptographicOperations.FixedTimeEquals(a, b);

    private static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    // ------------------------------------------------------------------ Random helpers

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    /// <summary>Creates a new 16-character lowercase hex device id.</summary>
    public static string NewDeviceId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>Creates a base64url nonce (no padding) with the protocol-mandated 16 random bytes.</summary>
    public static string NewNonce() => Base64Url(RandomNumberGenerator.GetBytes(ProtocolConstants.NonceLength));

    // ------------------------------------------------------------------ Content hashes

    /// <summary>Base64 SHA-256 of a request body, or the empty-body constant when there is no body.</summary>
    public static string HashContent(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
            return ProtocolConstants.EmptyBodySha256Base64;
        return Convert.ToBase64String(SHA256.HashData(body));
    }

    /// <summary>Base64url encoding without padding (used for nonces and the audit chain hash).</summary>
    public static string Base64Url(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    /// <summary>Serialises a 32-bit integer big-endian (used inside wrapped secret plaintexts).</summary>
    public static byte[] BigEndianBytes(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        return b;
    }
}

/// <summary>
/// A sealed AES-GCM payload. All three members are base64 on the wire.
/// </summary>
/// <param name="Nonce">12-byte GCM nonce.</param>
/// <param name="Ciphertext">Ciphertext bytes.</param>
/// <param name="Tag">16-byte authentication tag.</param>
public readonly record struct WrappedSecret(byte[] Nonce, byte[] Ciphertext, byte[] Tag)
{
    public string NonceBase64 => Convert.ToBase64String(Nonce);
    public string CiphertextBase64 => Convert.ToBase64String(Ciphertext);
    public string TagBase64 => Convert.ToBase64String(Tag);
}
