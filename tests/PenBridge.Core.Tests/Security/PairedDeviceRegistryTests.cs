using PenBridge.Core.Security;

namespace PenBridge.Core.Tests.Security;

public class PairedDeviceRegistryTests
{
    private static readonly Guid A = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid B = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void Add_list_key_touch_remove_and_persist()
    {
        var time = new FakeTime();
        var store = new InMemorySecretStore();
        var registry = new PairedDeviceRegistry(store, time);
        byte[] keyA = KeyScheduleTests.Fill(32, 1);

        registry.Add(A, "Galaxy S23 Ultra ✍", keyA);
        time.Advance(TimeSpan.FromMinutes(1));
        registry.Add(B, "Emulator", KeyScheduleTests.Fill(32, 2));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(registry.Touch(A));
        Assert.False(registry.Touch(Guid.NewGuid()));

        var reloaded = new PairedDeviceRegistry(store, time);
        IReadOnlyList<PairedDevice> list = reloaded.List();
        Assert.Equal(2, list.Count);
        Assert.Equal(A, list[0].DeviceId);
        Assert.Equal("Galaxy S23 Ultra ✍", list[0].Name);
        Assert.Equal(time.GetUtcNow().AddMinutes(-2), list[0].PairedAt);
        Assert.Equal(time.GetUtcNow(), list[0].LastSeen);

        byte[] key = new byte[32];
        Assert.True(reloaded.TryGetDeviceKey(A, key));
        Assert.Equal(keyA, key);

        Assert.True(reloaded.Remove(A));
        Assert.False(reloaded.Remove(A));
        Assert.False(new PairedDeviceRegistry(store, time).TryGetDeviceKey(A, key));
        Assert.Single(new PairedDeviceRegistry(store, time).List());
    }

    [Fact]
    public void Re_pairing_replaces_key()
    {
        var registry = new PairedDeviceRegistry(new InMemorySecretStore(), new FakeTime());
        registry.Add(A, "old", KeyScheduleTests.Fill(32, 1));
        registry.Add(A, "new", KeyScheduleTests.Fill(32, 7));
        Assert.Single(registry.List());
        Assert.Equal("new", registry.List()[0].Name);
        byte[] key = new byte[32];
        registry.TryGetDeviceKey(A, key);
        Assert.Equal(KeyScheduleTests.Fill(32, 7), key);
    }

    [Fact]
    public void Validates_inputs()
    {
        var registry = new PairedDeviceRegistry(new InMemorySecretStore());
        Assert.Throws<ArgumentException>(() => registry.Add(A, "x", new byte[31]));
        Assert.Throws<ArgumentException>(() => registry.Add(A, new string('é', 129), new byte[32]));
        Assert.Throws<ArgumentException>(() => registry.TryGetDeviceKey(A, new byte[16]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("02")]           // unknown version
    [InlineData("0100")]         // short header
    [InlineData("010001")]       // count 1, no entry
    [InlineData("0100ff")]       // too many
    [InlineData("01000000")]     // trailing byte
    public void Corrupt_records_are_rejected(string hex)
    {
        var store = new InMemorySecretStore();
        store.Set(PairedDeviceRegistry.StoreKey, Convert.FromHexString(hex));
        Assert.Throws<InvalidDataException>(() => new PairedDeviceRegistry(store));
    }

    [Fact]
    public void Truncated_and_duplicate_records_are_rejected()
    {
        var store = new InMemorySecretStore();
        new PairedDeviceRegistry(store).Add(A, "a", new byte[32]);
        Assert.True(store.TryGet(PairedDeviceRegistry.StoreKey, out byte[]? good));

        for (int cut = 3; cut < good.Length; cut++)
        {
            store.Set(PairedDeviceRegistry.StoreKey, good.AsSpan(0, cut));
            Assert.Throws<InvalidDataException>(() => new PairedDeviceRegistry(store));
        }

        byte[] dup = [1, 0, 2, .. good.AsSpan(3), .. good.AsSpan(3)];
        store.Set(PairedDeviceRegistry.StoreKey, dup);
        Assert.Throws<InvalidDataException>(() => new PairedDeviceRegistry(store));
    }
}
