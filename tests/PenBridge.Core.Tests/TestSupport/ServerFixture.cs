using System.Security.Cryptography;
using PenBridge.Core.Net;
using PenBridge.Core.Security;

[assembly: AssemblyFixture(typeof(PenBridge.Core.Tests.ServerFixture))]

namespace PenBridge.Core.Tests;

/// <summary>
/// Assembly fixture (one per test run) for the setup network tests share but don't test: the server identity and
/// the seeded pairing behind <c>NetHarness.ConnectPairedAsync</c>. Tests about identity or pairing create their own
/// (<c>ServerIdentityTests</c>, <c>NetHarness.PairAsync</c>).
/// Creating a PKCS#12 identity costs most of a second on macOS (keychain), so it starts in the background when the
/// run starts, overlapping the first tests, and only the first network test waits for it. The relay identity is
/// created on first use: most runs that need it are a few classes.
/// Reach it with <see cref="Current"/> (any test) or a constructor parameter.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private readonly Lazy<ServerIdentity> _relayIdentity = new(CreateIdentity);
    private Task<ServerIdentity>? _identity;

    /// <summary>The fixture of the running test (assembly fixtures exist before any test starts, so this doesn't wait).</summary>
    public static ServerFixture Current =>
        TestContext.Current.GetFixture<ServerFixture>().AsTask().GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("ServerFixture is only available inside a test.");

    /// <summary>The server identity every harness shares (tests don't depend on distinct server certificates).</summary>
    public ServerIdentity Identity => (_identity ?? throw new InvalidOperationException("Fixture not initialized.")).GetAwaiter().GetResult();

    /// <summary>A second identity for the TLS relay (a man in the middle must present another certificate).</summary>
    public ServerIdentity RelayIdentity => _relayIdentity.Value;

    public ValueTask InitializeAsync()
    {
        _identity = Task.Run(CreateIdentity);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Pairs a device without J-PAKE: registers a fresh device key in <paramref name="registry"/> and stores it on the
    /// client side of <paramref name="clientStore"/> under <see cref="Identity"/>'s fingerprint, as a completed PIN
    /// pairing would. A fresh key per call, so two seeded devices never share one.
    /// </summary>
    public void SeedPairing(IPairedDeviceRegistry registry, ISecretStore clientStore, string deviceName = "Test Phone")
    {
        var credentials = new ClientCredentialStore(clientStore);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        registry.Add(credentials.GetOrCreateDeviceId(), deviceName, key);
        credentials.SaveDeviceKey(Identity.Fingerprint, key);
        CryptographicOperations.ZeroMemory(key);
    }

    public async ValueTask DisposeAsync()
    {
        if (_identity is not null)
        {
            (await _identity.ConfigureAwait(false)).Dispose();
        }

        if (_relayIdentity.IsValueCreated)
        {
            _relayIdentity.Value.Dispose();
        }
    }

    private static ServerIdentity CreateIdentity() => ServerIdentity.FromPkcs12(ServerIdentity.CreatePkcs12(DateTimeOffset.UtcNow));
}
