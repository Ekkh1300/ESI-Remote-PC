using System.Security.Cryptography;
using System.Text;
using Esi.Protocol;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// The crypto core. Every one of these is a case where a subtle defect does not throw, it
/// silently weakens authentication — so they are tested against published vectors wherever
/// one exists rather than only against round-trips that would pass with a self-consistent
/// but wrong implementation.
/// </summary>
public sealed class EsiCryptoTests
{
    // ---------------------------------------------------------------------------
    // HKDF — RFC 5869 test vectors
    // ---------------------------------------------------------------------------

    /// <summary>
    /// RFC 5869 Appendix A.1: basic test case with SHA-256.
    /// <para>
    /// A hand-written HKDF that is subtly wrong — a swapped extract, a counter that starts
    /// at 0, a reversed concatenation — still round-trips perfectly against itself. Only an
    /// externally published vector catches that. This is the whole reason the vector is here.
    /// </para>
    /// </summary>
    [Fact]
    public void Hkdf_matches_rfc5869_test_case_1()
    {
        var ikm = new byte[]
        {
            0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b,
            0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b,
            0x0b, 0x0b,
        };
        var salt = new byte[]
        {
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0a, 0x0b, 0x0c,
        };
        var info = new byte[] { 0xf0, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9 };

        var prk = EsiCrypto.HkdfExtract(salt, ikm);
        Assert.Equal(
            "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5",
            Convert.ToHexStringLower(prk));

        var okm = EsiCrypto.HkdfExpand(prk, info, 42);
        Assert.Equal(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            Convert.ToHexStringLower(okm));
    }

    /// <summary>RFC 5869 Appendix A.3: the zero-length salt and info case.</summary>
    [Fact]
    public void Hkdf_matches_rfc5869_test_case_3()
    {
        // IKM is 22 octets of 0x0b; salt and info are both empty.
        var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        var salt = Array.Empty<byte>();
        var info = Array.Empty<byte>();

        var prk = EsiCrypto.HkdfExtract(salt, ikm);
        Assert.Equal("19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04", Convert.ToHexStringLower(prk));

        var okm = EsiCrypto.HkdfExpand(prk, info, 42);
        Assert.Equal(
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8",
            Convert.ToHexStringLower(okm));
    }

    [Fact]
    public void Hkdf_expand_rejects_an_out_of_range_length()
    {
        var prk = EsiCrypto.HkdfExtract(new byte[] { 1 }, new byte[] { 2 });

        Assert.Throws<ArgumentOutOfRangeException>(() => EsiCrypto.HkdfExpand(prk, [], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EsiCrypto.HkdfExpand(prk, [], 255 * 32 + 1));
    }

    [Fact]
    public void Hkdf_expand_across_multiple_blocks_is_a_prefix_of_a_longer_expansion()
    {
        // RFC 5869's counter construction must produce a stream, not independent blocks:
        // asking for 64 bytes and truncating has to equal asking for 32 bytes.
        var prk = EsiCrypto.HkdfExtract("salt"u8.ToArray(), "ikm"u8.ToArray());
        var info = "info"u8.ToArray();

        var short32 = EsiCrypto.HkdfExpand(prk, info, 32);
        var long64 = EsiCrypto.HkdfExpand(prk, info, 64);

        Assert.Equal(short32, long64[..32]);
    }

    // ---------------------------------------------------------------------------
    // Key hierarchy
    // ---------------------------------------------------------------------------

    [Fact]
    public void Derived_keys_are_distinct_purposes()
    {
        var secret = EsiCrypto.RandomBytes(ProtocolConstants.SecretLength);
        const string device = "abc123";

        var session = EsiCrypto.DeriveSessionKey(secret, device, 1);
        var wrap = EsiCrypto.DeriveWrapKey(secret, device);
        var audit = EsiCrypto.DeriveAuditKey(secret, device, 1);
        var provision = EsiCrypto.DeriveProvisioningKey(secret, "pair1");

        var all = new[] { session, wrap, audit, provision }
            .Select(Convert.ToHexStringLower)
            .ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Session_key_depends_on_the_key_id()
    {
        var secret = EsiCrypto.RandomBytes(ProtocolConstants.SecretLength);

        Assert.NotEqual(
            EsiCrypto.DeriveSessionKey(secret, "dev", 1),
            EsiCrypto.DeriveSessionKey(secret, "dev", 2));
    }

    [Fact]
    public void Device_id_is_case_insensitive_in_the_key_derivation()
    {
        // The verifier lowercases the device id before use, so a client that uppercased it
        // must still derive the same key rather than silently failing to authenticate.
        var secret = EsiCrypto.RandomBytes(ProtocolConstants.SecretLength);

        Assert.Equal(
            EsiCrypto.DeriveSessionKey(secret, "ABCDEF0123456789", 3),
            EsiCrypto.DeriveSessionKey(secret, "abcdef0123456789", 3));
    }

    [Fact]
    public void DeriveKey_produces_the_requested_length()
    {
        var secret = EsiCrypto.RandomBytes(ProtocolConstants.SecretLength);

        Assert.Equal(16, EsiCrypto.DeriveKey(secret, "label", 16).Length);
        Assert.Equal(64, EsiCrypto.DeriveKey(secret, "label", 64).Length);
        Assert.Equal(32, EsiCrypto.DeriveKey(secret, "label").Length);
    }

    // ---------------------------------------------------------------------------
    // The canonical string — the contract with the phone
    // ---------------------------------------------------------------------------

    [Fact]
    public void Canonical_string_is_eight_lf_separated_lines_with_no_trailing_newline()
    {
        var canonical = EsiCrypto.BuildCanonicalString(
            "post", "/api/v1/power/shutdown", 1_700_000_000, "nonce123",
            "hash456", "dev1", 7);

        var lines = canonical.Split('\n');

        Assert.Equal(8, lines.Length);
        Assert.Equal(ProtocolConstants.SignatureDomain, lines[0]);
        Assert.Equal("POST", lines[1]);
        Assert.Equal("/api/v1/power/shutdown", lines[2]);
        Assert.Equal("1700000000", lines[3]);
        Assert.Equal("nonce123", lines[4]);
        Assert.Equal("hash456", lines[5]);
        Assert.Equal("dev1", lines[6]);
        Assert.Equal("7", lines[7]);

        Assert.DoesNotContain('\r', canonical);
        Assert.False(canonical.EndsWith('\n'));
    }

    /// <summary>
    /// A golden vector. The mobile client builds the same string independently, in Dart, and
    /// the two must agree byte for byte. If this test's expected value is ever regenerated
    /// from the implementation it stops being a test, so it is a literal.
    /// </summary>
    [Fact]
    public void Canonical_string_matches_the_documented_golden_vector()
    {
        var canonical = EsiCrypto.BuildCanonicalString(
            "get", "/api/v1/system/metrics", 1_700_000_000, "AAAAAAAAAAAAAAAAAAAAAA",
            "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=", "0123456789abcdef", 1);

        Assert.Equal(
            "ESIV1\nGET\n/api/v1/system/metrics\n1700000000\nAAAAAAAAAAAAAAAAAAAAAA\n" +
            "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=\n0123456789abcdef\n1",
            canonical);
    }

    [Fact]
    public void Canonical_string_uppercases_the_method_and_lowercases_the_device_id()
    {
        var canonical = EsiCrypto.BuildCanonicalString(
            "delete", "/x", 1, "n", "h", "DEADBEEF", 0);

        Assert.Contains("\nDELETE\n", canonical);
        Assert.EndsWith("\ndeadbeef\n0", canonical);
    }

    // ---------------------------------------------------------------------------
    // Canonical target: path + sorted query
    // ---------------------------------------------------------------------------

    [Fact]
    public void Canonical_target_sorts_query_segments_ordinally()
    {
        Assert.Equal(
            "/api/v1/files?a=1&b=2&z=3",
            EsiCrypto.BuildCanonicalTarget("/api/v1/files", "z=3&a=1&b=2"));
    }

    [Fact]
    public void Canonical_target_tolerates_a_leading_question_mark()
    {
        // The server hands the verifier the query with or without '?' depending on the
        // request; the canonical form must not depend on that.
        Assert.Equal(
            EsiCrypto.BuildCanonicalTarget("/p", "a=1"),
            EsiCrypto.BuildCanonicalTarget("/p", "?a=1"));
    }

    [Fact]
    public void Canonical_target_with_no_query_is_just_the_path()
    {
        Assert.Equal("/api/v1/health", EsiCrypto.BuildCanonicalTarget("/api/v1/health", null));
        Assert.Equal("/api/v1/health", EsiCrypto.BuildCanonicalTarget("/api/v1/health", ""));
        Assert.Equal("/api/v1/health", EsiCrypto.BuildCanonicalTarget("/api/v1/health", "?"));
    }

    [Fact]
    public void Canonical_target_defaults_an_empty_path_to_root()
    {
        Assert.Equal("/", EsiCrypto.BuildCanonicalTarget("", null));
    }

    [Fact]
    public void Canonical_target_preserves_the_exact_query_bytes()
    {
        // Percent-encoding must not be normalised: re-encoding would change the bytes the
        // client signed, and the two would disagree on any value containing a space or a
        // reserved character.
        Assert.Equal(
            "/p?path=C%3A%5CMy%20Files",
            EsiCrypto.BuildCanonicalTarget("/p", "path=C%3A%5CMy%20Files"));
    }

    [Fact]
    public void Canonical_target_orders_repeated_keys_deterministically()
    {
        var first = EsiCrypto.BuildCanonicalTarget("/p", "a=2&a=1");
        var second = EsiCrypto.BuildCanonicalTarget("/p", "a=1&a=2");

        Assert.Equal(first, second);
    }

    // ---------------------------------------------------------------------------
    // Signing
    // ---------------------------------------------------------------------------

    [Fact]
    public void Sign_then_verify_succeeds_for_an_unchanged_request()
    {
        var key = EsiCrypto.RandomBytes(32);
        var canonical = EsiCrypto.BuildCanonicalString("GET", "/api/v1/health", 1_700_000_000,
            EsiCrypto.NewNonce(), EsiCrypto.HashContent([]), EsiCrypto.NewDeviceId(), 1);

        var signature = EsiCrypto.Sign(key, canonical);

        Assert.True(EsiCrypto.SignatureEquals(signature, signature));
    }

    [Fact]
    public void Any_change_to_the_canonical_string_invalidates_the_signature()
    {
        var key = EsiCrypto.RandomBytes(32);
        var device = EsiCrypto.NewDeviceId();
        var nonce = EsiCrypto.NewNonce();

        string Signed(string method, string path, long ts, string n, string hash, int keyId)
            => EsiCrypto.Sign(key, EsiCrypto.BuildCanonicalString(method, path, ts, n, hash, device, keyId));

        var baseline = Signed("GET", "/api/v1/health", 1_700_000_000, nonce,
            EsiCrypto.HashContent([]), 1);

        // A different method, path, timestamp, nonce, body hash or key id must all fail.
        // If any of these passed, the signature would not actually be binding the request.
        Assert.False(EsiCrypto.SignatureEquals(baseline,
            Signed("POST", "/api/v1/health", 1_700_000_000, nonce, EsiCrypto.HashContent([]), 1)));
        Assert.False(EsiCrypto.SignatureEquals(baseline,
            Signed("GET", "/api/v1/settings", 1_700_000_000, nonce, EsiCrypto.HashContent([]), 1)));
        Assert.False(EsiCrypto.SignatureEquals(baseline,
            Signed("GET", "/api/v1/health", 1_700_000_001, nonce, EsiCrypto.HashContent([]), 1)));
        Assert.False(EsiCrypto.SignatureEquals(baseline,
            Signed("GET", "/api/v1/health", 1_700_000_000, EsiCrypto.NewNonce(), EsiCrypto.HashContent([]), 1)));
        Assert.False(EsiCrypto.SignatureEquals(baseline,
            Signed("GET", "/api/v1/health", 1_700_000_000, nonce, EsiCrypto.HashContent("{}"u8), 1)));
        Assert.False(EsiCrypto.SignatureEquals(baseline,
            Signed("GET", "/api/v1/health", 1_700_000_000, nonce, EsiCrypto.HashContent([]), 2)));
    }

    [Fact]
    public void Signature_comparison_rejects_garbage_without_throwing()
    {
        var key = EsiCrypto.RandomBytes(32);
        var signature = EsiCrypto.Sign(key, "anything");

        Assert.False(EsiCrypto.SignatureEquals(signature, "not base64!!"));
        Assert.False(EsiCrypto.SignatureEquals("not base64!!", signature));
        Assert.False(EsiCrypto.SignatureEquals(signature, ""));
    }

    [Fact]
    public void Signature_comparison_rejects_a_different_length_signature()
    {
        var key = EsiCrypto.RandomBytes(32);
        var full = EsiCrypto.Sign(key, "anything");
        var truncated = Convert.ToBase64String(Convert.FromBase64String(full)[..16]);

        Assert.False(EsiCrypto.SignatureEquals(full, truncated));
    }

    // ---------------------------------------------------------------------------
    // Content hashing
    // ---------------------------------------------------------------------------

    [Fact]
    public void Empty_body_hash_is_the_documented_constant()
    {
        // This exact string is written in PROTOCOL.md and produced independently by the
        // phone. A change here is a protocol change, not a refactor.
        Assert.Equal(
            "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=",
            EsiCrypto.HashContent([]));

        Assert.Equal(
            Convert.ToBase64String(SHA256.HashData([])),
            EsiCrypto.HashContent(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Content_hash_distinguishes_different_bodies()
    {
        Assert.NotEqual(EsiCrypto.HashContent("a"u8), EsiCrypto.HashContent("b"u8));
        Assert.NotEqual(EsiCrypto.HashContent("{}"u8), EsiCrypto.HashContent("{ }"u8));
    }

    // ---------------------------------------------------------------------------
    // AES-256-GCM secret wrapping
    // ---------------------------------------------------------------------------

    [Fact]
    public void Seal_then_open_round_trips()
    {
        var key = EsiCrypto.RandomBytes(32);
        var plaintext = EsiCrypto.RandomBytes(ProtocolConstants.SecretLength);
        var aad = "esi-provision-v1|pair123"u8.ToArray();

        var wrapped = EsiCrypto.Seal(plaintext, key, aad);

        Assert.Equal(12, wrapped.Nonce.Length);
        Assert.Equal(16, wrapped.Tag.Length);
        Assert.Equal(plaintext.Length, wrapped.Ciphertext.Length);
        Assert.Equal(plaintext, EsiCrypto.Open(wrapped, key, aad));
    }

    [Fact]
    public void Seal_uses_a_fresh_nonce_every_time()
    {
        // A repeated nonce under one key is catastrophic for GCM: it leaks the XOR of two
        // plaintexts and breaks the authentication key entirely.
        var key = EsiCrypto.RandomBytes(32);
        var plaintext = new byte[16];

        var nonces = Enumerable.Range(0, 64)
            .Select(_ => Convert.ToHexStringLower(EsiCrypto.Seal(plaintext, key, []).Nonce))
            .ToList();

        Assert.Equal(64, nonces.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Open_rejects_a_tampered_ciphertext()
    {
        var key = EsiCrypto.RandomBytes(32);
        var wrapped = EsiCrypto.Seal("secret payload"u8, key, "aad"u8);

        var tampered = new WrappedSecret(wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag);
        tampered.Ciphertext[0] ^= 0x01;

        Assert.Throws<AuthenticationTagMismatchException>(() => EsiCrypto.Open(tampered, key, "aad"u8));
    }

    [Fact]
    public void Open_rejects_a_tampered_tag()
    {
        var key = EsiCrypto.RandomBytes(32);
        var wrapped = EsiCrypto.Seal("secret payload"u8, key, "aad"u8);

        var tampered = new WrappedSecret(wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag);
        tampered.Tag[15] ^= 0x80;

        Assert.Throws<AuthenticationTagMismatchException>(() => EsiCrypto.Open(tampered, key, "aad"u8));
    }

    [Fact]
    public void Open_rejects_the_wrong_key()
    {
        var wrapped = EsiCrypto.Seal("secret"u8, EsiCrypto.RandomBytes(32), []);

        Assert.Throws<AuthenticationTagMismatchException>(
            () => EsiCrypto.Open(wrapped, EsiCrypto.RandomBytes(32), []));
    }

    [Fact]
    public void Open_rejects_mismatched_associated_data()
    {
        // The associated data binds the ciphertext to the pairing attempt it belongs to, so a
        // secret captured from attempt A cannot be replayed into attempt B.
        var key = EsiCrypto.RandomBytes(32);
        var wrapped = EsiCrypto.Seal("secret"u8, key, "attempt-A"u8);

        Assert.Throws<AuthenticationTagMismatchException>(
            () => EsiCrypto.Open(wrapped, key, "attempt-B"u8));
    }

    // ---------------------------------------------------------------------------
    // base64url
    // ---------------------------------------------------------------------------

    [Fact]
    public void Base64Url_is_url_safe_and_unpadded()
    {
        // 0xFB 0xFF 0xFE encodes to '+/' in standard base64, which has no place in a URL
        // or an HTTP header.
        var data = new byte[] { 0xFB, 0xFF, 0xFE, 0x00 };
        var encoded = EsiCrypto.Base64Url(data);

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.Equal(data, EsiCrypto.FromBase64Url(encoded));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public void Base64Url_round_trips_at_every_padding_length(int length)
    {
        var data = EsiCrypto.RandomBytes(length);
        Assert.Equal(data, EsiCrypto.FromBase64Url(EsiCrypto.Base64Url(data)));
    }

    // ---------------------------------------------------------------------------
    // Identifiers
    // ---------------------------------------------------------------------------

    [Fact]
    public void NewDeviceId_is_16_lowercase_hex_characters()
    {
        var id = EsiCrypto.NewDeviceId();

        Assert.Equal(16, id.Length);
        Assert.All(id, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'), $"'{c}' is not lowercase hex"));
    }

    [Fact]
    public void NewDeviceId_does_not_repeat()
    {
        var ids = Enumerable.Range(0, 512).Select(_ => EsiCrypto.NewDeviceId()).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NewNonce_is_16_bytes_of_unpadded_base64url()
    {
        var nonce = EsiCrypto.NewNonce();

        Assert.Equal(ProtocolConstants.NonceLength, EsiCrypto.FromBase64Url(nonce).Length);
        Assert.DoesNotContain('=', nonce);
    }

    [Fact]
    public void NewNonce_does_not_repeat()
    {
        var nonces = Enumerable.Range(0, 512).Select(_ => EsiCrypto.NewNonce()).ToList();
        Assert.Equal(nonces.Count, nonces.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void BigEndianBytes_matches_the_documented_layout()
    {
        // Used inside wrapped plaintexts for the key id. Little-endian here would still be
        // self-consistent, so the byte order is asserted explicitly.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00 }, EsiCrypto.BigEndianBytes(256));
        Assert.Equal(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, EsiCrypto.BigEndianBytes(int.MaxValue));
    }

    // ---------------------------------------------------------------------------
    // Protocol constants
    // ---------------------------------------------------------------------------

    [Fact]
    public void Hkdf_salt_is_stable_across_the_protocol_version()
    {
        // Changing this would silently invalidate every paired device, because both halves
        // would derive different keys from the same master secret.
        Assert.Equal("ESI-RemotePC/v1", ProtocolConstants.HkdfSalt);
        Assert.Equal("ESIV1", ProtocolConstants.SignatureDomain);
        Assert.Equal(1, ProtocolConstants.Version);
    }

    [Fact]
    public void Protocol_string_constants_are_utf8_ascii()
    {
        // The canonical string is hashed as UTF-8. Any non-ASCII in a constant would be a
        // byte-count vs. character-count trap on one side of the wire.
        foreach (var value in new[] { ProtocolConstants.HkdfSalt, ProtocolConstants.SignatureDomain })
        {
            Assert.Equal(value.Length, Encoding.UTF8.GetByteCount(value));
            Assert.All(value, c => Assert.InRange(c, (char)0x20, (char)0x7E));
        }
    }
}
