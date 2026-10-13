using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using PenBridge.Core.Net;
using PenBridge.Core.Security;

namespace PenBridge.Android.Services;

/// <summary>
/// Wraps the Keystore store so the main thread doesn't pay a keystore decrypt per UI refresh: it remembers whether each
/// key is present (<see cref="Contains"/>, used for "is this Mac paired") and caches the device id value. Device keys
/// themselves are never cached; every <see cref="TryGet"/> for them goes to the inner store.
/// </summary>
/// <remarks>
/// Every write goes through this wrapper (Core's pairing and Unpaired handling included, since the client is built on
/// it), so <see cref="Set"/>/<see cref="Delete"/> keep the cache exact. Thread-safe.
/// </remarks>
public sealed class CachingSecretStore : ISecretStore
{
    private readonly ISecretStore _inner;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, bool> _present = new(StringComparer.Ordinal);
    private byte[]? _deviceId;
    private long _writes;

    public CachingSecretStore(ISecretStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>True when a readable value is stored under <paramref name="key"/>. Decrypts at most once per key until it changes.</summary>
    public bool Contains(string key)
    {
        lock (_lock)
        {
            if (_present.TryGetValue(key, out bool known))
            {
                return known;
            }
        }

        bool found = TryGet(key, out byte[]? value);
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }

        return found;
    }

    /// <summary>True when this phone holds a device key for the server with this fingerprint (cached).</summary>
    public bool IsPaired(ReadOnlySpan<byte> serverFingerprint) =>
        serverFingerprint.Length == CertificateFingerprint.Length
        && Contains(ClientCredentialStore.ServerKeyPrefix + CertificateFingerprint.ToBase64Url(serverFingerprint));

    /// <inheritdoc />
    public bool TryGet(string key, [NotNullWhen(true)] out byte[]? value)
    {
        bool isDeviceId = key == ClientCredentialStore.DeviceIdKey;
        if (isDeviceId)
        {
            lock (_lock)
            {
                if (_deviceId is not null)
                {
                    value = (byte[])_deviceId.Clone();
                    return true;
                }
            }
        }

        long writes;
        lock (_lock)
        {
            writes = _writes;
        }

        bool found = _inner.TryGet(key, out value);
        lock (_lock)
        {
            if (writes != _writes)
            {
                return found; // a write raced this read; don't cache a possibly stale answer
            }

            _present[key] = found;
            if (isDeviceId && found)
            {
                _deviceId = (byte[])value!.Clone();
            }
        }

        return found;
    }

    /// <inheritdoc />
    public void Set(string key, ReadOnlySpan<byte> value)
    {
        lock (_lock)
        {
            _writes++;
            _present.Remove(key);
            if (key == ClientCredentialStore.DeviceIdKey)
            {
                _deviceId = null;
            }
        }

        _inner.Set(key, value);
        lock (_lock)
        {
            _writes++;
            _present[key] = true;
            if (key == ClientCredentialStore.DeviceIdKey)
            {
                _deviceId = value.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Delete(string key)
    {
        lock (_lock)
        {
            _writes++;
            _present.Remove(key);
            if (key == ClientCredentialStore.DeviceIdKey)
            {
                _deviceId = null;
            }
        }

        _inner.Delete(key);
        lock (_lock)
        {
            _writes++;
            _present[key] = false;
        }
    }
}
