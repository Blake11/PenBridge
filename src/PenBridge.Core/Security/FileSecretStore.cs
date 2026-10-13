using System.Diagnostics.CodeAnalysis;

namespace PenBridge.Core.Security;

/// <summary>
/// <see cref="ISecretStore"/> that keeps one file per key in a directory. Intended for tests and
/// the SimClient only: values are protected by file permissions alone (directory 700, files 600 on
/// Unix), not encrypted at rest. Platform heads must use the OS secret store instead.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const string Extension = ".secret";

    private readonly string _directory;
    private readonly Lock _lock = new();

    /// <summary>Creates the store, creating <paramref name="directory"/> (mode 700) if needed.</summary>
    public FileSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
        }
        else
        {
            Directory.CreateDirectory(_directory, DirectoryMode);
            File.SetUnixFileMode(_directory, DirectoryMode);
        }
    }

    /// <summary>Absolute path of the backing directory.</summary>
    public string DirectoryPath => _directory;

    /// <inheritdoc />
    public bool TryGet(string key, [NotNullWhen(true)] out byte[]? value)
    {
        string path = PathFor(key);
        lock (_lock)
        {
            try
            {
                value = File.ReadAllBytes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                value = null;
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void Set(string key, ReadOnlySpan<byte> value)
    {
        string path = PathFor(key);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }

        lock (_lock)
        {
            try
            {
                using (var stream = new FileStream(temp, options))
                {
                    stream.Write(value);
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

    private string PathFor(string key)
    {
        SecretStoreKey.Validate(key);
        return Path.Combine(_directory, key + Extension);
    }
}
