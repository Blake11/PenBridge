using System.Security.Cryptography;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>
/// The phone's pairing state in an <see cref="ISecretStore"/>: its own device id, and one <c>K_dev</c> per paired
/// server, keyed by the server's pinned certificate fingerprint. Holding a key for a fingerprint is what "paired
/// with that Mac" means. Thread-safe as far as the store is.
/// </summary>
public sealed class ClientCredentialStore
{
    /// <summary>Store key of the 16-byte device id.</summary>
    public const string DeviceIdKey = "pb.client.device-id";

    /// <summary>Prefix of per-server keys; the suffix is the base64url fingerprint.</summary>
    public const string ServerKeyPrefix = "pb.client.server.";

    private readonly ISecretStore _store;
    private readonly Lock _lock = new();

    public ClientCredentialStore(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Returns the persisted device id, creating a random one on first use.</summary>
    public Guid GetOrCreateDeviceId()
    {
        lock (_lock)
        {
            if (_store.TryGet(DeviceIdKey, out byte[]? stored) && stored.Length == 16)
            {
                return new Guid(stored, bigEndian: true);
            }

            Span<byte> id = stackalloc byte[16];
            RandomNumberGenerator.Fill(id);
            _store.Set(DeviceIdKey, id);
            return new Guid(id, bigEndian: true);
        }
    }

    /// <summary>True when a device key is stored for the server with this fingerprint.</summary>
    public bool IsPaired(ReadOnlySpan<byte> serverFingerprint)
    {
        Span<byte> key = stackalloc byte[KeySchedule.KeyLength];
        bool found = TryGetDeviceKey(serverFingerprint, key);
        CryptographicOperations.ZeroMemory(key);
        return found;
    }

    /// <summary>Copies the 32-byte <c>K_dev</c> for the server; false when not paired.</summary>
    public bool TryGetDeviceKey(ReadOnlySpan<byte> serverFingerprint, Span<byte> destination)
    {
        if (destination.Length < KeySchedule.KeyLength)
        {
            throw new ArgumentException("Destination must hold 32 bytes.", nameof(destination));
        }

        if (!_store.TryGet(KeyFor(serverFingerprint), out byte[]? key))
        {
            return false;
        }

        try
        {
            if (key.Length != KeySchedule.KeyLength)
            {
                return false;
            }

            key.CopyTo(destination);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Stores (or replaces) the device key for the server.</summary>
    public void SaveDeviceKey(ReadOnlySpan<byte> serverFingerprint, ReadOnlySpan<byte> deviceKey)
    {
        if (deviceKey.Length != KeySchedule.KeyLength)
        {
            throw new ArgumentException("Device key must be 32 bytes.", nameof(deviceKey));
        }

        _store.Set(KeyFor(serverFingerprint), deviceKey);
    }

    /// <summary>Forgets the server (deletes its device key).</summary>
    public void Forget(ReadOnlySpan<byte> serverFingerprint) => _store.Delete(KeyFor(serverFingerprint));

    private static string KeyFor(ReadOnlySpan<byte> fingerprint)
    {
        if (fingerprint.Length != CertificateFingerprint.Length)
        {
            throw new ArgumentException("Fingerprint must be 32 bytes.", nameof(fingerprint));
        }

        return ServerKeyPrefix + CertificateFingerprint.ToBase64Url(fingerprint);
    }
}
