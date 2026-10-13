using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace PenBridge.Core.Security;

/// <summary>Volatile <see cref="ISecretStore"/> for tests and in-process demos.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public bool TryGet(string key, [NotNullWhen(true)] out byte[]? value)
    {
        SecretStoreKey.Validate(key);
        lock (_lock)
        {
            if (_values.TryGetValue(key, out byte[]? stored))
            {
                value = (byte[])stored.Clone();
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <inheritdoc />
    public void Set(string key, ReadOnlySpan<byte> value)
    {
        SecretStoreKey.Validate(key);
        byte[] copy = value.ToArray();
        lock (_lock)
        {
            if (_values.TryGetValue(key, out byte[]? old))
            {
                CryptographicOperations.ZeroMemory(old);
            }

            _values[key] = copy;
        }
    }

    /// <inheritdoc />
    public void Delete(string key)
    {
        SecretStoreKey.Validate(key);
        lock (_lock)
        {
            if (_values.Remove(key, out byte[]? old))
            {
                CryptographicOperations.ZeroMemory(old);
            }
        }
    }
}
