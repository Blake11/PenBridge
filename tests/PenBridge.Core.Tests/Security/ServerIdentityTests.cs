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
        var time = new ManualTimeProvider();
        using ServerIdentity identity = ServerIdentity.LoadOrCreate(new InMemorySecretStore(), time);
        X509Certificate2 cert = identity.Certificate;

        Assert.StartsWith("CN=PenBridge-", cert.Subject, StringComparison.Ordinal);
        Assert.Equal(cert.Subject, cert.Issuer);
        Assert.True(cert.HasPrivateKey);
        using ECDsa? key = cert.GetECDsaPublicKey();
        Assert.NotNull(key);
        Assert.Equal(256, key.KeySize);
        Assert.True(cert.NotAfter.ToUniversalTime() >= time.GetUtcNow().AddYears(10).AddDays(-1).UtcDateTime);
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
        Assert.True(parsed.SequenceEqual(identity.Fingerprint));
        Assert.True(CertificateFingerprint.Matches(identity.Certificate, parsed));

        parsed[0] ^= 1;
        Assert.False(CertificateFingerprint.Matches(identity.Certificate, parsed));
        Assert.False(CertificateFingerprint.Matches(identity.Certificate, parsed[..31]));
        Assert.False(CertificateFingerprint.Matches(null, parsed));
        Assert.False(CertificateFingerprint.TryParseBase64Url("AAAA", parsed));
        Assert.False(CertificateFingerprint.TryParseBase64Url("not base64!", parsed));
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
        Assert.True(clientSsl.SslProtocol >= SslProtocols.Tls12);
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
