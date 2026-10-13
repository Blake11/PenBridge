using System.Diagnostics.CodeAnalysis;

namespace PenBridge.Core.Security;

/// <summary>
/// Persistent store for small secrets (server identity, device keys). Platform heads back it with
/// the Keychain / Android Keystore; Core ships <see cref="InMemorySecretStore"/> and
/// <see cref="FileSecretStore"/> for tests and the SimClient.
/// </summary>
/// <remarks>
/// Keys are validated by <see cref="SecretStoreKey.Validate"/>: 1–128 chars of <c>[A-Za-z0-9._-]</c>,
/// not starting with '.'. Implementations must be safe to call from multiple threads.
/// </remarks>
public interface ISecretStore
{
    /// <summary>Returns a copy of the stored value, or <see langword="false"/> if the key is absent.</summary>
    bool TryGet(string key, [NotNullWhen(true)] out byte[]? value);

    /// <summary>Creates or replaces the value stored under <paramref name="key"/>.</summary>
    [SuppressMessage("Naming", "CA1716", Justification = "Name fixed by the Phase 1 spec; C#-only consumers.")]
    void Set(string key, ReadOnlySpan<byte> value);

    /// <summary>Removes the value; a no-op if the key is absent.</summary>
    void Delete(string key);
}

/// <summary>Key validation shared by all <see cref="ISecretStore"/> implementations.</summary>
public static class SecretStoreKey
{
    /// <summary>Maximum key length in characters.</summary>
    public const int MaxLength = 128;

    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="key"/> is a valid store key.</summary>
    public static void Validate(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is 0 or > MaxLength || key[0] == '.')
        {
            throw new ArgumentException("Secret key must be 1-128 characters and not start with '.'.", nameof(key));
        }

        foreach (char c in key)
        {
            bool ok = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';
            if (!ok)
            {
                throw new ArgumentException("Secret key may only contain [A-Za-z0-9._-].", nameof(key));
            }
        }
    }
}
