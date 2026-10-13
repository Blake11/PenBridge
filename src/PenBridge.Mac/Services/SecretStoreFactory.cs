using Foundation;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Security;

namespace PenBridge.Mac.Services;

/// <summary>
/// Picks the app's <see cref="ISecretStore"/>: the Keychain in Release builds, a development file store in Debug builds.
/// </summary>
/// <remarks>
/// <para>Why Debug avoids the Keychain (issue #55): the login keychain stamps each item's partition list with the
/// creating build's cdhash when the code has no Apple Team ID, and the self-signed "PenBridge Dev" certificate has
/// none. Every recompile gets a new cdhash, so every read of every item prompts the user, even though the designated
/// requirement stays the same. The app can't widen the partition list (that needs the keychain password).</para>
/// <para>The Debug store is <see cref="FileSecretStore"/> in <see cref="DevelopmentDirectory"/> (directory 0700,
/// files 0600, not encrypted at rest). It is a separate store: a Debug build has its own server identity and paired
/// devices, and never reads or writes the Keychain items of a Release build, nor the other way round. A phone paired
/// with a Debug build must pair again with a Release build. Release builds always use
/// <see cref="KeychainSecretStore"/>.</para>
/// </remarks>
public static partial class SecretStoreFactory
{
    /// <summary>Directory name under <c>~/Library/Application Support</c> for development-only state.</summary>
    public const string DevelopmentFolderName = "PenBridge-Dev";

    /// <summary><c>~/Library/Application Support/PenBridge-Dev/secrets</c>: the Debug build's secret store.</summary>
    public static string DevelopmentDirectory
    {
        get
        {
            NSUrl[] urls = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User);
            string root = urls.Length > 0 && urls[0].Path is { Length: > 0 } path
                ? path
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
            return Path.Combine(root, DevelopmentFolderName, "secrets");
        }
    }

    /// <summary>Creates the store for this build configuration and logs which one is active.</summary>
    public static ISecretStore Create(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        ILogger log = loggers.CreateLogger(typeof(SecretStoreFactory).FullName!);
#if DEBUG
        var store = new FileSecretStore(DevelopmentDirectory);
        Log.DevelopmentStore(log, store.DirectoryPath);
        return store;
#else
        Log.KeychainStore(log, KeychainSecretStore.Service);
        return new KeychainSecretStore();
#endif
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Information, "Debug build: using development file secret store (not Keychain) at {Directory}")]
        public static partial void DevelopmentStore(ILogger logger, string directory);

        [LoggerMessage(2, LogLevel.Information, "Using Keychain secret store (service {Service})")]
        public static partial void KeychainStore(ILogger logger, string service);
    }
}
