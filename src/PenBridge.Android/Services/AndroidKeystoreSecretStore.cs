using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using CipherMode = Javax.Crypto.CipherMode;
using System.Text;
using Android.Security.Keystore;
using Android.Util;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using PenBridge.Core.Security;

namespace PenBridge.Android.Services;

/// <summary>
/// <see cref="ISecretStore"/> backed by an AES-256-GCM key in AndroidKeyStore (alias <see cref="KeyAlias"/>; not
/// exportable, no user authentication). Each secret is a file <c>FilesDir/secrets/&lt;key&gt;.bin</c> (mode 0600)
/// holding <c>nonce(12) ‖ ciphertext ‖ tag(16)</c>; the store key is bound as AAD, so a file renamed to another key
/// fails to decrypt.
/// </summary>
/// <remarks>
/// <para>Nonces are fresh and random: the keystore key requires randomized encryption, so the keystore generates the
/// IV for every <c>Set</c>. A missing, truncated, corrupt or undecryptable file (e.g. the keystore key was lost after a
/// restore) counts as absent: <see cref="TryGet"/> returns false (logged by key name) and the caller re-pairs.</para>
/// <para>The key is StrongBox-backed where the device has a secure element, else TEE-backed. If <c>Set</c> finds the
/// key unusable (permanently invalidated, unrecoverable, provider failure), it deletes the alias, generates a new key
/// and retries once.</para>
/// <para>Thread-safe (one lock); calls block on file I/O and the keystore (StrongBox operations take milliseconds),
/// so keep them off the hot input path and cache results (<see cref="CachingSecretStore"/>). Never logs values.</para>
/// </remarks>
public sealed class AndroidKeystoreSecretStore : ISecretStore
{
    /// <summary>AndroidKeyStore alias of the wrapping key.</summary>
    public const string KeyAlias = "penbridge.secrets";

    private const string KeyStoreProvider = "AndroidKeyStore";
    private const string Tag = "PenBridge";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaxFileLength = 64 * 1024;
    private const string Extension = ".bin";
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _directory;
    private readonly Lock _lock = new();
    private IKey? _key;

    /// <summary>Creates the store in <c>&lt;filesDir&gt;/secrets</c> (pass <c>Context.FilesDir.AbsolutePath</c>).</summary>
    public AndroidKeystoreSecretStore(string filesDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(filesDir);
        _directory = Path.Combine(filesDir, "secrets");
        Directory.CreateDirectory(_directory, DirectoryMode);
        File.SetUnixFileMode(_directory, DirectoryMode);
    }

    /// <inheritdoc />
    public bool TryGet(string key, [NotNullWhen(true)] out byte[]? value)
    {
        string path = PathFor(key);
        value = null;
        lock (_lock)
        {
            byte[] blob;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < NonceLength + TagLength || info.Length > MaxFileLength)
                {
                    return false;
                }

                blob = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogUnreadable(key, ex);
                return false;
            }

            if (blob.Length < NonceLength + TagLength)
            {
                return false;
            }

            try
            {
                Cipher cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(TagLength * 8, blob, 0, NonceLength));
                cipher.UpdateAAD(Encoding.UTF8.GetBytes(key));
                value = cipher.DoFinal(blob, NonceLength, blob.Length - NonceLength);
                return value is not null;
            }
            catch (Exception ex) when (ex is Java.Lang.Throwable or CryptographicException)
            {
                // Every keystore/crypto failure (AEADBadTagException for a tampered or foreign file, a lost or
                // invalidated key, ProviderException, KeyStoreException, ...) means the value can't be trusted: missing.
                LogUnreadable(key, ex);
                value = null;
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void Set(string key, ReadOnlySpan<byte> value)
    {
        string path = PathFor(key);
        byte[] plain = value.ToArray();
        try
        {
            lock (_lock)
            {
                try
                {
                    SealAndWrite(key, path, plain);
                }
                catch (Exception ex) when (IsKeyFailure(ex))
                {
                    // The wrapping key is unusable (invalidated, unrecoverable, provider error): replace it and retry
                    // once. Values sealed under the old key were already unreadable, so they now read as missing.
                    Log.Warn(Tag, "Keystore key unusable (" + ex.GetType().Name + "); regenerating");
                    DeleteKeyAlias();
                    SealAndWrite(key, path, plain);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <inheritdoc />
    public void Delete(string key)
    {
        string path = PathFor(key);
        lock (_lock)
        {
            File.Delete(path);
        }
    }

    private void SealAndWrite(string key, string path, byte[] plain)
    {
        Cipher cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        byte[] nonce = cipher.GetIV() ?? throw new CryptographicException("Keystore cipher returned no IV.");
        if (nonce.Length != NonceLength)
        {
            throw new CryptographicException("Unexpected GCM nonce length.");
        }

        cipher.UpdateAAD(Encoding.UTF8.GetBytes(key));
        byte[] sealedBytes = cipher.DoFinal(plain) ?? throw new CryptographicException("Keystore cipher returned no output.");
        WriteAtomically(path, nonce, sealedBytes);
    }

    private static bool IsKeyFailure(Exception ex) =>
        ex is KeyPermanentlyInvalidatedException or InvalidKeyException or UnrecoverableKeyException
            or KeyStoreException or ProviderException;

    private void DeleteKeyAlias()
    {
        _key = null;
        try
        {
            KeyStore store = KeyStore.GetInstance(KeyStoreProvider)!;
            store.Load(null);
            store.DeleteEntry(KeyAlias);
        }
        catch (Exception ex) when (ex is Java.Lang.Throwable)
        {
            Log.Warn(Tag, "Could not delete the keystore key (" + ex.GetType().Name + ")");
        }
    }

    private static void LogUnreadable(string key, Exception ex) =>
        Log.Warn(Tag, "Secret '" + key + "' unreadable (" + ex.GetType().Name + "); treating it as missing");

    private static void WriteAtomically(string path, byte[] nonce, byte[] sealedBytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = FileMode,
        };
        try
        {
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(nonce);
                stream.Write(sealedBytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private IKey GetOrCreateKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        KeyStore store = KeyStore.GetInstance(KeyStoreProvider)!;
        store.Load(null);
        if (store.GetKey(KeyAlias, null) is { } existing)
        {
            return _key = existing;
        }

        try
        {
            return _key = GenerateKey(strongBox: true);
        }
        catch (StrongBoxUnavailableException)
        {
            // No StrongBox secure element on this device: fall back to the TEE-backed keystore.
            return _key = GenerateKey(strongBox: false);
        }
    }

    private static IKey GenerateKey(bool strongBox)
    {
        KeyGenerator generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreProvider)!;
        KeyGenParameterSpec spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .SetRandomizedEncryptionRequired(true)!
            .SetUserAuthenticationRequired(false)!
            .SetIsStrongBoxBacked(strongBox)!
            .Build();
        generator.Init(spec);
        return generator.GenerateKey()!;
    }

    private string PathFor(string key)
    {
        SecretStoreKey.Validate(key);
        return Path.Combine(_directory, key + Extension);
    }
}
