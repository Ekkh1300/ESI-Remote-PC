using System.Text;
using Esi.Protocol;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// The same vectors the Flutter app asserts, expressed against the C# implementation.
/// <para>
/// Two implementations of a wire protocol that is tested independently will drift. Each side
/// can be perfectly self-consistent - a canonical string both halves build identically, a
/// key derived the same wrong way on both - and the product still fails at runtime, in
/// production, on a signed request. Sharing one set of literal values across both test
/// suites is the only thing that turns "they should match" into something a machine checks.
/// </para>
/// <para>
/// These values are duplicated verbatim from
/// <c>android-app/esi_remote/test/protocol_golden_test.dart</c>. If you change one, change
/// both, and treat a mismatch as a protocol change rather than a test to be updated.
/// </para>
/// </summary>
public sealed class CrossImplementationGoldenTests
{
    /// <summary>The device identity used throughout PROTOCOL.md 3.1 and 4.2.</summary>
    private const string DeviceId = "3f2a9c17d4b8e206";

    /// <summary>0x00..0x1f, fixed so the derivation can be checked by hand.</summary>
    private static byte[] MasterSecret => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    /// <summary>0x20..0x3f.</summary>
    private static byte[] ProvisioningSecret => Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();

    private const long Timestamp = 1_756_000_000;
    private const string Nonce = "f3Q8Zl0aW1kJ7w3K";
    private const string PairingId = "b21f0c9d8e7a6b5c";

    [Fact]
    public void DeriveSessionKey_matches_the_app()
        => Assert.Equal(
            "+cfGI98S26cbOjGTJtYAulmCRjSL58QQJORChRHvXB4=",
            Convert.ToBase64String(EsiCrypto.DeriveSessionKey(MasterSecret, DeviceId, 1)));

    [Fact]
    public void DeriveWrapKey_matches_the_app()
        => Assert.Equal(
            "zufKdgrBzKvo0m0XXfikGlwxqGBiUpaHcMUO7e3TTMU=",
            Convert.ToBase64String(EsiCrypto.DeriveWrapKey(MasterSecret, DeviceId)));

    [Fact]
    public void DeriveAuditKey_matches_the_app()
        => Assert.Equal(
            "a3onjZVUZ7X/wSIILDQ3KNUdS0QyZiYK0eQLvxRXSF4=",
            Convert.ToBase64String(EsiCrypto.DeriveAuditKey(MasterSecret, DeviceId, 1)));

    [Fact]
    public void DeriveProvisioningKey_matches_the_app()
        => Assert.Equal(
            "VD3QGQ8Vy844FnaCCKHor3r4ua/IVnXa6HvHssjeKwE=",
            Convert.ToBase64String(EsiCrypto.DeriveProvisioningKey(ProvisioningSecret, PairingId)));

    [Fact]
    public void The_signed_simple_request_matches_the_app()
    {
        var sessionKey = EsiCrypto.DeriveSessionKey(MasterSecret, DeviceId, 1);

        var canonical = EsiCrypto.BuildCanonicalString(
            "GET", "/api/v1/system/info?detail=full", Timestamp, Nonce,
            ProtocolConstants.EmptyBodySha256Base64, DeviceId, 1);

        Assert.Equal("3IgBbqX92+lvNq8roQ22p4USyn8+CgMxCwb6OGrnDto=", EsiCrypto.Sign(sessionKey, canonical));
    }

    [Fact]
    public void The_signed_query_heavy_request_matches_the_app()
    {
        var sessionKey = EsiCrypto.DeriveSessionKey(MasterSecret, DeviceId, 1);

        var canonical = EsiCrypto.BuildCanonicalString(
            "GET", "/api/v1/files/search?a=1&a=2&maxResults=50&query=a&root=C%3A%5C",
            Timestamp, Nonce, ProtocolConstants.EmptyBodySha256Base64, DeviceId, 12);

        Assert.Equal("hNBeWVEj92bLuC8zlqNphVlKlOqqK0C5o8slvCFuq/4=", EsiCrypto.Sign(sessionKey, canonical));
    }

    [Fact]
    public void The_signed_request_with_a_body_matches_the_app()
    {
        var sessionKey = EsiCrypto.DeriveSessionKey(MasterSecret, DeviceId, 1);
        var body = Encoding.UTF8.GetBytes("""{"enabled":true,"profileId":"performance"}""");

        Assert.Equal("18QKl3TUP/rmFyQfKm8JgHzfn/53Pii/gvzyRAi7QZs=", EsiCrypto.HashContent(body));

        var canonical = EsiCrypto.BuildCanonicalString(
            "POST", "/api/v1/gaming/mode", Timestamp, Nonce,
            EsiCrypto.HashContent(body), DeviceId, 1);

        Assert.Equal("pKec3mxgJ5BwjHRmv3vhMxKFz7P9MJIm7rnYQaC3rJ0=", EsiCrypto.Sign(sessionKey, canonical));
    }

    [Fact]
    public void Base64Url_matches_the_app()
    {
        Assert.Equal("_____________________w",
            EsiCrypto.Base64Url(Enumerable.Repeat((byte)0xff, 16).ToArray()));

        Assert.Equal("-_--ABA", EsiCrypto.Base64Url([0xfb, 0xff, 0xbe, 0x00, 0x10]));
    }

    /// <summary>
    /// The AES-GCM pairing envelope the app opens in its own tests.
    /// <para>
    /// The one assertion that the key hierarchy, the associated data and the plaintext layout
    /// all agree across the two implementations. Every layer above it can be right and this
    /// still fail if the AAD string differs by a character.
    /// </para>
    /// </summary>
    [Fact]
    public void The_pairing_envelope_opens_on_this_side_too()
    {
        var wrapped = new WrappedSecret(
            Convert.FromBase64String("QEFCQ0RFRkdISUpL"),
            Convert.FromBase64String("006FjGld21jBdnZLe+kHEov4zxcnlVuxhO5+zz821RpF0fTc"),
            Convert.FromBase64String("qzbhnvGwb/jGUVxC+wv8zA=="));

        var key = EsiCrypto.DeriveProvisioningKey(ProvisioningSecret, PairingId);
        var aad = Encoding.UTF8.GetBytes($"esi-pair-v1|{PairingId}|{DeviceId}");

        var plaintext = EsiCrypto.Open(wrapped, key, aad);

        Assert.Equal(MasterSecret, plaintext[..ProtocolConstants.SecretLength]);

        // 32 bytes of secret then a big-endian key id, matching the app's parser.
        var keyId = (plaintext[32] << 24) | (plaintext[33] << 16) | (plaintext[34] << 8) | plaintext[35];
        Assert.Equal(1, keyId);
    }

    /// <summary>
    /// The rotation envelope. Key derivation plus a different associated-data string, so a
    /// mismatch in either shows up here and not only in the pairing path.
    /// </summary>
    [Fact]
    public void The_rotation_envelope_opens_on_this_side_too()
    {
        var wrapped = new WrappedSecret(
            Convert.FromBase64String("oKGio6Slpqeoqaqr"),
            Convert.FromBase64String("AV/fapKkygbGx9eC+Ed/28gFXIn09XM32IQM6wBrgR+9ItOX"),
            Convert.FromBase64String("UxEBcrFNogkis0oYxXGQ2g=="));

        var key = EsiCrypto.DeriveWrapKey(MasterSecret, DeviceId);
        var aad = Encoding.UTF8.GetBytes($"esi-rotate-v1|{DeviceId}");

        var plaintext = EsiCrypto.Open(wrapped, key, aad);

        Assert.Equal(
            Enumerable.Range(0x80, 32).Select(i => (byte)i).ToArray(),
            plaintext[..ProtocolConstants.SecretLength]);
    }
}
