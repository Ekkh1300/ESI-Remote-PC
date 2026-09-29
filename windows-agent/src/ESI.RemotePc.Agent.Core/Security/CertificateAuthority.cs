using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Esi.Core.Security;

/// <summary>
/// Owns the agent's TLS identity.
/// <para>
/// The agent cannot use a publicly-trusted certificate: it is a self-signed certificate
/// served from a private IP on a LAN. The security therefore rests on
/// <b>pinning</b> rather than on the public PKI, which is a normal and sound design for
/// this situation — the client verifies the SPKI SHA-256 fingerprint it learned during
/// pairing and nothing else is accepted.
/// </para>
/// <para>
/// The certificate is generated once, persisted as a PFX with an exportable private key
/// protected by DPAPI-NG (machine scope), and reloaded on every subsequent start. If it is
/// deleted, the pin changes, and every paired device must re-pair — which is the correct
/// and safe failure mode, not a bug to work around.
/// </para>
/// </summary>
public sealed class CertificateAuthority(ILogger<CertificateAuthority> log, AgentOptions options) : IDisposable
{
    private const int ValidityDays = 825; // just under a year, so a leaked key ages out

    /// <summary>
    /// DPAPI entropy. Binding the ciphertext to this label means a blob produced by a
    /// different application cannot be decrypted by this one, even under the same account.
    /// </summary>
    private static readonly byte[] DpapiEntropy = System.Text.Encoding.UTF8.GetBytes("ESI-RemotePC/v1/tls");

    /// <summary>
    /// The account the agent runs as, decided once. It matters twice:
    /// in production the agent is LocalSystem, so the private key goes in the machine key
    /// store and the PFX is protected with DPAPI machine scope; in console mode it is an
    /// ordinary user, where the machine key store is denied, so the key goes in the user
    /// key store and DPAPI is called with user scope.
    /// <para>
    /// Kestrel requires a certificate whose private key is <em>persisted</em> in a key
    /// store — it rejects an <c>EphemeralKeySet</c> certificate outright — so this choice is
    /// a hard requirement rather than a preference, and the fallback is not optional.
    /// </para>
    /// </summary>
    private static readonly bool IsLocalSystem = DetectLocalSystem();

    private static DataProtectionScope DpapiScope =>
        IsLocalSystem ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;

    private static X509KeyStorageFlags KeyStorageFlags =>
        IsLocalSystem
            ? X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable;

    private static bool DetectLocalSystem()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return string.Equals(identity.User?.Value, "S-1-5-18", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private readonly Lock _gate = new();
    private X509Certificate2? _certificate;

    public string CertificatePath => options.CertificatePath;

    /// <summary>
    /// Base64 SHA-256 over the DER-encoded SubjectPublicKeyInfo. This is the pin the client
    /// stores and compares; it is a public value.
    /// </summary>
    public string? SpkiPin
    {
        get
        {
            // SubjectPublicKeyInfo is what the certificate actually signs, so pinning it
            // survives a reissue with a new serial or validity window.
            var cert = Certificate;
            return cert is null ? null : ComputePin(cert);
        }
    }

    public string? Subject
    {
        get
        {
            var cert = Certificate;
            return cert?.Subject;
        }
    }

    public DateTimeOffset? NotBefore => Certificate?.NotBefore.ToUniversalTime();
    public DateTimeOffset? NotAfter => Certificate?.NotAfter.ToUniversalTime();

    /// <summary>The certificate for Kestrel, or <c>null</c> if generation failed.</summary>
    public X509Certificate2? Certificate
    {
        get
        {
            lock (_gate)
            {
                if (_certificate is not null)
                    return _certificate;

                _certificate = Load() ?? Generate();
                return _certificate;
            }
        }
    }

    private X509Certificate2? Load()
    {
        try
        {
            if (!File.Exists(CertificatePath))
                return null;

            // The PFX is wrapped with DPAPI machine scope, so the file on disk is useless
            // to any other Windows account or any other machine.
            var protectedBytes = File.ReadAllBytes(CertificatePath);
            var pfx = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DpapiScope);

            // The key store is chosen by account (see KeyStorageFlags). Kestrel needs a
            // persisted key; which store it lands in depends on whether the agent is
            // running as LocalSystem or as an ordinary user in console mode.
            var certificate = new X509Certificate2(pfx, (string?)null, KeyStorageFlags);

            if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
                DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
            {
                log.LogWarning("The stored TLS certificate has expired; generating a replacement");
                certificate.Dispose();
                return null;
            }

            log.LogInformation("Loaded the TLS identity from {Path}", CertificatePath);
            return certificate;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not load the stored TLS certificate; generating a replacement");
            return null;
        }
    }

    private X509Certificate2? Generate()
    {
        try
        {
            var directory = options.ResolveDataDirectory();
            Directory.CreateDirectory(directory);

            var hostName = SafeMachineName();
            var subject = new X500DistinguishedName($"CN={hostName}");

            // ECDSA P-256: smaller keys and handshakes than RSA-2048, and universally
            // supported by current Android and Windows TLS stacks.
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(hostName);
            sanBuilder.AddDnsName(hostName + ".local");
            sanBuilder.AddDnsName("esiremotepc-" + options.Settings.Port + ".local");

            // Include every current address so a client connecting by IP gets a name that
            // matches the certificate.
            foreach (var address in LocalAddresses())
                sanBuilder.AddIpAddress(address);

            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);

            // Marked as a server certificate; the client is not authenticated by TLS,
            // because it authenticates itself with a signed request instead.
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], // serverAuth
                critical: false));

            request.CertificateExtensions.Add(sanBuilder.Build());

            // A basic-constraints CA flag would be wrong here: this is an end-entity
            // certificate that happens to be self-signed, and the client pins it directly.
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            var now = DateTimeOffset.UtcNow;
            using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(ValidityDays));

            // Round-trip through PFX so the certificate carries an associated private key
            // in the appropriate key store, which is what Kestrel requires.
            var pfx = certificate.Export(X509ContentType.Pfx);
            var persisted = new X509Certificate2(pfx, (string?)null, KeyStorageFlags);

            // The stored blob is DPAPI-protected, so the PFX on disk is useless to any
            // other account (and, in machine scope, useless on any other machine).
            var protectedBytes = ProtectedData.Protect(pfx, DpapiEntropy, DpapiScope);
            File.WriteAllBytes(CertificatePath, protectedBytes);

            log.LogInformation("Generated a new TLS identity for {Subject}; SPKI pin {Pin}", persisted.Subject, ComputePin(persisted));
            return persisted;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not generate a TLS certificate. The agent will start with HTTP only, " +
                             "which means request signing is the only protection in place.");
            return null;
        }
    }

    private static string ComputePin(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
            return Convert.ToBase64String(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

        using var ecdsa = certificate.GetECDsaPublicKey();
        return ecdsa is not null
            ? Convert.ToBase64String(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()))
            : string.Empty;
    }

    private static IPAddress[] LocalAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Where(a => !IPAddress.IsLoopback(a))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static string SafeMachineName()
    {
        try
        {
            var name = Environment.MachineName;
            return string.IsNullOrWhiteSpace(name) ? "esi-remotepc" : name;
        }
        catch
        {
            return "esi-remotepc";
        }
    }

    /// <summary>Deletes the stored identity. Every paired device will need to re-pair.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _certificate?.Dispose();
            _certificate = null;

            try
            {
                if (File.Exists(CertificatePath))
                    File.Delete(CertificatePath);

                log.LogInformation("The TLS identity was reset. Paired devices must pair again.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not delete {Path}", CertificatePath);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _certificate?.Dispose();
            _certificate = null;
        }
    }
}
