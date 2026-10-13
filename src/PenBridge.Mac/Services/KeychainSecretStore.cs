using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Foundation;
using PenBridge.Core.Security;
using Security;

namespace PenBridge.Mac.Services;

/// <summary>
/// <see cref="ISecretStore"/> backed by Keychain generic-password items: service <see cref="Service"/>, account = key.
/// Holds the server PKCS#12 identity and the paired-device registry (device keys).
/// </summary>
/// <remarks>
/// <para>Items live in the login keychain (the file-based keychain). The data-protection keychain would need a
/// keychain-access-groups entitlement backed by a provisioning profile, which a self-signed build can't have.
/// The login keychain is never synced to iCloud, so items stay on this Mac. <c>kSecAttrAccessible</c> has no effect
/// in the file-based keychain (the login keychain's lock state governs access); it is set to "after first unlock,
/// this device only" so the intent carries over if the items ever move to the data-protection keychain.</para>
/// <para>Access control (issue #55) has two layers, and a read needs both without a prompt:</para>
/// <list type="bullet">
/// <item><description>The ACL's trusted-application list stores the creating app's designated requirement.
/// Builds signed with the stable "PenBridge Dev" certificate share one requirement
/// (<c>identifier "com.blake11.penbridge" and certificate root = H"…"</c>); an ad-hoc build's requirement is its
/// cdhash, so every rebuild adds a new entry after the user allows it.</description></item>
/// <item><description>The partition list (<c>partition_id</c> ACL entry). securityd stamps it from the caller:
/// <c>teamid:&lt;TEAMID&gt;</c> for Apple-issued certificates, but <c>cdhash:&lt;cdhash&gt;</c> for code without a
/// Team ID, which includes the self-signed dev certificate. So even a "PenBridge Dev" build that recompiled anything
/// is outside the partition and macOS asks again; "Always Allow" appends the new cdhash. An incremental build
/// that rebuilt nothing keeps its cdhash and does not ask. An explicit <c>SecAccess</c> at creation still gets the
/// cdhash partition, and changing the partition list needs the keychain password, so the app cannot widen it.
/// <see cref="Set"/> on an existing item resets its partition list to the writing build. A Team ID (an Apple Development
/// or Developer ID certificate) should give a stable <c>teamid:</c> partition (not tested).</description></item>
/// </list>
/// <para>Thread-safe: the Security framework is, and <see cref="Set"/> serializes its add-or-update.</para>
/// </remarks>
public sealed class KeychainSecretStore : ISecretStore
{
    /// <summary>The <c>kSecAttrService</c> of every item.</summary>
    public const string Service = "com.blake11.penbridge";

    private readonly Lock _writeLock = new();
    private readonly string _service;

    public KeychainSecretStore()
        : this(Service)
    {
    }

    /// <summary>Uses another service name (scratch checks; production code uses <see cref="Service"/>).</summary>
    public KeychainSecretStore(string service)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        _service = service;
    }

    /// <inheritdoc />
    public bool TryGet(string key, [NotNullWhen(true)] out byte[]? value)
    {
        SecretStoreKey.Validate(key);
        using SecRecord query = Query(key);
        using NSData? data = SecKeyChain.QueryAsData(query, false, out SecStatusCode status);
        if (status == SecStatusCode.ItemNotFound)
        {
            value = null;
            return false;
        }

        ThrowOnError(status, "read");
        value = data?.ToArray() ?? [];
        return true;
    }

    /// <inheritdoc />
    public void Set(string key, ReadOnlySpan<byte> value)
    {
        SecretStoreKey.Validate(key);
        byte[] copy = value.ToArray();
        try
        {
            using NSData data = NSData.FromArray(copy);
            lock (_writeLock)
            {
                using SecRecord query = Query(key);
                using var update = new SecRecord { ValueData = data };
                SecStatusCode status = SecKeyChain.Update(query, update);
                if (status == SecStatusCode.ItemNotFound)
                {
                    using SecRecord item = Query(key);
                    item.Label = "PenBridge (" + key + ")";
                    item.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
                    item.ValueData = data;
                    status = SecKeyChain.Add(item);
                }

                ThrowOnError(status, "write");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    /// <inheritdoc />
    public void Delete(string key)
    {
        SecretStoreKey.Validate(key);
        using SecRecord query = Query(key);
        SecStatusCode status = SecKeyChain.Remove(query);
        if (status != SecStatusCode.ItemNotFound)
        {
            ThrowOnError(status, "delete");
        }
    }

    private SecRecord Query(string key) => new(SecKind.GenericPassword)
    {
        Service = _service,
        Account = key,
        Synchronizable = false,
    };

    private static void ThrowOnError(SecStatusCode status, string operation)
    {
        if (status != SecStatusCode.Success)
        {
            // The status code is not secret; keys and values never go into the message.
            throw new CryptographicException($"Keychain {operation} failed: {status} ({(int)status}).");
        }
    }
}
