using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PenBridge.Core.Security;

/// <summary>
/// The Mac server's TLS identity: a self-signed ECDSA P-256 certificate (CN = PenBridge-&lt;random&gt;,
/// valid for 10 years), persisted as PKCS#12 bytes through an <see cref="ISecretStore"/>.
/// </summary>
/// <remarks>
/// The certificate is always loaded from its PKCS#12 bytes (never used straight from
/// <see cref="CertificateRequest.CreateSelfSigned"/>) with the default key-storage flags, because
/// <c>SslStream</c> on macOS cannot use a server certificate with an ephemeral key set.
/// </remarks>
public sealed class ServerIdentity : IDisposable
{
    /// <summary>Secret-store key holding the PKCS#12 bytes.</summary>
    public const string StoreKey = "pb.server-identity.pfx";

    /// <summary>Certificate validity period.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(3652);

    private readonly byte[] _fingerprint;

    private ServerIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        _fingerprint = CertificateFingerprint.Compute(certificate);
        FingerprintBase64Url = CertificateFingerprint.ToBase64Url(_fingerprint);
    }

    /// <summary>The certificate with its private key, usable as an <c>SslStream</c> server certificate.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 fingerprint of the certificate (32 bytes).</summary>
    public ReadOnlySpan<byte> Fingerprint => _fingerprint;

    /// <summary>Unpadded base64url fingerprint, for the TXT record (<c>fp=</c>) and QR payload.</summary>
    public string FingerprintBase64Url { get; }

    /// <summary>Loads the identity from <paramref name="store"/>, creating and persisting one on first run.</summary>
    public static ServerIdentity LoadOrCreate(ISecretStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.TryGet(StoreKey, out byte[]? pfx))
        {
            try
            {
                return FromPkcs12(pfx);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }

        byte[] created = CreatePkcs12((timeProvider ?? TimeProvider.System).GetUtcNow());
        try
        {
            store.Set(StoreKey, created);
            return FromPkcs12(created);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(created);
        }
    }

    /// <summary>Creates a fresh identity as PKCS#12 bytes (not persisted).</summary>
    public static byte[] CreatePkcs12(DateTimeOffset now)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        var subject = new X500DistinguishedName("CN=PenBridge-" + Convert.ToHexStringLower(random));

        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now + Validity);
        return certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password: null);
    }

    /// <summary>Loads an identity from PKCS#12 bytes; throws if it has no ECDSA private key.</summary>
    public static ServerIdentity FromPkcs12(ReadOnlySpan<byte> pfx)
    {
        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.DefaultKeySet);
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new CryptographicException("Server identity has no private key.");
        }

        using (ECDsa? key = certificate.GetECDsaPrivateKey())
        {
            if (key is null)
            {
                certificate.Dispose();
                throw new CryptographicException("Server identity is not an ECDSA certificate.");
            }
        }

        return new ServerIdentity(certificate);
    }

    /// <inheritdoc />
    public void Dispose() => Certificate.Dispose();
}
