using PenBridge.Core.Security;

namespace PenBridge.Core.Tests.Security;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    public static TheoryData<string> Stores => new() { "memory", "file" };

    private ISecretStore Create(string kind) => kind == "memory" ? new InMemorySecretStore() : new FileSecretStore(_dir);

    [Theory]
    [MemberData(nameof(Stores))]
    public void Set_get_overwrite_delete_round_trip(string kind)
    {
        ISecretStore store = Create(kind);
        Assert.False(store.TryGet("a.key", out _));

        store.Set("a.key", [1, 2, 3]);
        Assert.True(store.TryGet("a.key", out byte[]? value));
        Assert.Equal([1, 2, 3], value);

        value[0] = 99; // returned array is a copy
        store.Set("a.key", [4]);
        Assert.True(store.TryGet("a.key", out value));
        Assert.Equal([4], value);

        store.Delete("a.key");
        store.Delete("a.key");
        Assert.False(store.TryGet("a.key", out _));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void Rejects_invalid_keys(string kind)
    {
        ISecretStore store = Create(kind);
        foreach (string bad in new[] { "", "../escape", "a/b", ".hidden", "a b", new string('x', 129), "a\\b" })
        {
            Assert.Throws<ArgumentException>(() => store.Set(bad, [1]));
            Assert.Throws<ArgumentException>(() => store.TryGet(bad, out _));
        }
    }

    [Fact]
    public void File_store_uses_owner_only_permissions_and_persists()
    {
        var store = new FileSecretStore(_dir);
        store.Set("k", [7, 7]);

        Assert.True(new FileSecretStore(_dir).TryGet("k", out byte[]? value));
        Assert.Equal([7, 7], value);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "k.secret")));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(_dir));
        }

        Assert.Single(Directory.GetFiles(_dir)); // no temp files left behind
    }
}
