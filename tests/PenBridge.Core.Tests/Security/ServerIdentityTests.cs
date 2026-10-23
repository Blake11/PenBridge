using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PenBridge.Core.Security;

namespace PenBridge.Core.Tests.Security;

public class ServerIdentityTests
{
    [Fact]
    public void Creates_p256_self_signed_cert_valid_for_ten_years()
    {
        var time = new FakeTime();
        using ServerIdentity identity = ServerIdentity.LoadOrCreate(new InMemorySecretStore(), time);
        X509Certificate2 cert = identity.Certificate;

        Assert.StartsWith("CN=PenBridge-", cert.Subject, StringComparison.Ordinal);
        Assert.Equal(cert.Subject, cert.Issuer);
        Assert.True(cert.HasPrivateKey);
        using ECDsa? key = cert.GetECDsaPublicKey();
        Assert.NotNull(key);
        Assert.Equal(256, key.KeySize);
        Assert.InRange(cert.NotAfter.ToUniversalTime(), time.GetUtcNow().AddYears(10).AddDays(-1).UtcDateTime, DateTime.MaxValue);
        Assert.Equal(SHA256.HashData(cert.RawData), identity.Fingerprint.ToArray());
        Assert.Equal(43, identity.FingerprintBase64Url.Length);
    }

    [Fact]
    public void Persists_and_reloads_same_identity()
    {
        var store = new InMemorySecretStore();
        byte[] first;
        using (ServerIdentity a = ServerIdentity.LoadOrCreate(store))
        {
            first = a.Fingerprint.ToArray();
        }

        Assert.True(store.TryGet(ServerIdentity.StoreKey, out _));
        using ServerIdentity b = ServerIdentity.LoadOrCreate(store);
        Assert.Equal(first, b.Fingerprint.ToArray());
        Assert.True(b.Certificate.HasPrivateKey);
    }

    [Fact]
    public void Fingerprint_base64url_round_trips_and_matches_constant_time()
    {
        using ServerIdentity identity = ServerIdentity.LoadOrCreate(new InMemorySecretStore());
        Span<byte> parsed = stackalloc byte[32];
        Assert.True(CertificateFingerprint.TryParseBase64Url(identity.FingerprintBase64Url, parsed));
        Assert.Equal(identity.Fingerprint, parsed);
        Assert.True(CertificateFingerprint.Matches(identity.Certificate, parsed));

        parsed[0] ^= 1;
        Assert.False(CertificateFingerprint.Matches(identity.Certificate, parsed));
        Assert.False(CertificateFingerprint.Matches(identity.Certificate, parsed[..31]));
        Assert.False(CertificateFingerprint.Matches(null, parsed));
        Assert.False(CertificateFingerprint.TryParseBase64Url("AAAA", parsed));
        Assert.False(CertificateFingerprint.TryParseBase64Url("not base64!", parsed));
    }

    [Fact]
    public void Fingerprint_of_der_bytes_writes_into_the_destination()
    {
        X509Certificate2 cert = ServerFixture.Current.Identity.Certificate;
        byte[] wide = new byte[40];
        CertificateFingerprint.Compute(cert.RawData, wide);
        Assert.Equal(CertificateFingerprint.Compute(cert), wide[..32]);
        Assert.Equal(new byte[8], wide[32..]);

        var ex = Assert.Throws<ArgumentException>(() => CertificateFingerprint.Compute(cert.RawData, new byte[31]));
        Assert.Equal("destination", ex.ParamName);
    }

    [Fact]
    public void Fingerprint_parsing_checks_text_length_and_destination()
    {
        byte[] fp = KeyScheduleTests.Fill(32, 7);
        string text = CertificateFingerprint.ToBase64Url(fp);
        Assert.Equal(43, text.Length);
        byte[] parsed = new byte[32];

        Assert.False(CertificateFingerprint.TryParseBase64Url(text, new byte[31]));
        Assert.True(CertificateFingerprint.TryParseBase64Url(text, new byte[33]));

        // The decoder skips whitespace, so only the length cap stops long text that still decodes to 32 bytes.
        Assert.True(CertificateFingerprint.TryParseBase64Url(text + new string(' ', 64 - 43), parsed));
        Assert.Equal(fp, parsed);
        Assert.False(CertificateFingerprint.TryParseBase64Url(text + new string(' ', 65 - 43), parsed));

        Assert.False(CertificateFingerprint.TryParseBase64Url(text + "A", parsed));   // 44 chars: 33 bytes
        Assert.False(CertificateFingerprint.TryParseBase64Url(text.Remove(42), parsed));   // 31 bytes
        Assert.False(CertificateFingerprint.TryParseBase64Url(text.Remove(42) + "!", parsed));
    }

    [Fact]
    public void Pkcs12_without_a_private_key_is_rejected()
    {
        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(ServerFixture.Current.Identity.Certificate.RawData);
        byte[] pfx = publicOnly.Export(X509ContentType.Pkcs12);

        var ex = Assert.Throws<CryptographicException>(() => ServerIdentity.FromPkcs12(pfx));
        Assert.Contains("no private key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pkcs12_with_a_non_ecdsa_key_is_rejected()
    {
        DateTimeOffset now = new FakeTime().GetUtcNow();
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=not-ecdsa", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        byte[] pfx = cert.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password: null);

        var ex = Assert.Throws<CryptographicException>(() => ServerIdentity.FromPkcs12(pfx));
        Assert.Contains("not an ECDSA", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loaded_pkcs12_bytes_are_zeroed()
    {
        var inner = new InMemorySecretStore();
        inner.Set(ServerIdentity.StoreKey, ServerIdentity.CreatePkcs12(new FakeTime().GetUtcNow()));
        var store = new PairedDeviceRegistryTests.HandOutStore(inner);

        using ServerIdentity identity = ServerIdentity.LoadOrCreate(store);
        Assert.NotNull(store.LastHandedOut);
        Assert.Equal(new byte[store.LastHandedOut.Length], store.LastHandedOut);
    }

    [Fact]
    public async Task Reloaded_identity_serves_tls_over_loopback_with_fingerprint_pin()
    {
        var store = new InMemorySecretStore();
        ServerIdentity.LoadOrCreate(store).Dispose();
        using ServerIdentity identity = ServerIdentity.LoadOrCreate(store); // the persisted-then-loaded path
        byte[] pin = identity.Fingerprint.ToArray();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task serverTask = Task.Run(
            async () =>
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync(cts.Token);
                await using var ssl = new SslStream(accepted.GetStream());
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions { ServerCertificate = identity.Certificate, ClientCertificateRequired = false },
                    cts.Token);
                byte[] buffer = new byte[4];
                await ssl.ReadExactlyAsync(buffer, cts.Token);
                await ssl.WriteAsync(buffer, cts.Token);
            },
            cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await using var clientSsl = new SslStream(client.GetStream());
        await clientSsl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "penbridge",
                RemoteCertificateValidationCallback = (_, cert, _, _) => CertificateFingerprint.Matches(cert, pin),
            },
            cts.Token);

        Assert.True(clientSsl.IsAuthenticated);
        TestContext.Current.TestOutputHelper?.WriteLine($"Negotiated TLS protocol: {clientSsl.SslProtocol}");
        Assert.True(
            clientSsl.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13,
            $"Expected TLS 1.2+ (ADR-0013), got {clientSsl.SslProtocol}");
        byte[] echo = [1, 2, 3, 4];
        await clientSsl.WriteAsync(echo, cts.Token);
        byte[] back = new byte[4];
        await clientSsl.ReadExactlyAsync(back, cts.Token);
        Assert.Equal(echo, back);
        await serverTask;
    }

    [Fact]
    public async Task Client_pin_rejects_a_different_server_certificate()
    {
        using ServerIdentity real = ServerIdentity.LoadOrCreate(new InMemorySecretStore());
        using ServerIdentity impostor = ServerIdentity.LoadOrCreate(new InMemorySecretStore());
        byte[] pin = real.Fingerprint.ToArray();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serverTask = Task.Run(
            async () =>
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync(cts.Token);
                await using var ssl = new SslStream(accepted.GetStream());
                try
                {
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = impostor.Certificate }, cts.Token);
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException)
                {
                }
            },
            cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await using var clientSsl = new SslStream(client.GetStream());
        await Assert.ThrowsAsync<AuthenticationException>(() => clientSsl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "penbridge",
                RemoteCertificateValidationCallback = (_, cert, _, _) => CertificateFingerprint.Matches(cert, pin),
            },
            cts.Token));
        await serverTask;
    }
}
