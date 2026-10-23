using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
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

    [Fact]
    public void At_most_max_devices_can_be_paired_but_a_known_one_can_re_pair()
    {
        var store = new InMemorySecretStore();
        var registry = new PairedDeviceRegistry(store, new FakeTime());
        for (int i = 0; i < PairedDeviceRegistry.MaxDevices; i++)
        {
            registry.Add(Id(i), "d" + i, KeyScheduleTests.Fill(32, (byte)i));
        }

        Assert.Throws<InvalidOperationException>(() => registry.Add(Id(PairedDeviceRegistry.MaxDevices), "one too many", new byte[32]));
        registry.Add(Id(0), "re-paired", KeyScheduleTests.Fill(32, 200));

        IReadOnlyList<PairedDevice> list = new PairedDeviceRegistry(store).List();
        Assert.Equal(PairedDeviceRegistry.MaxDevices, list.Count);
        Assert.Equal("re-paired", list[^1].Name);
        Assert.DoesNotContain(list, d => d.DeviceId == Id(PairedDeviceRegistry.MaxDevices));
    }

    [Fact]
    public void Record_fields_decode_at_their_limits()
    {
        var pairedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        var lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_456_789);
        string longest = new('n', PairedDeviceRegistry.MaxNameBytes);
        var store = new InMemorySecretStore();
        store.Set(PairedDeviceRegistry.StoreKey, Record(
            Entry(A, "", pairedAt.ToUnixTimeMilliseconds(), lastSeen.ToUnixTimeMilliseconds(), 1),
            Entry(B, longest, 0, 0, 2)));

        var registry = new PairedDeviceRegistry(store);
        IReadOnlyList<PairedDevice> list = registry.List();
        Assert.Equal(2, list.Count);
        Assert.Equal(new PairedDevice(A, "", pairedAt, lastSeen), list[0]);
        Assert.Equal(new PairedDevice(B, longest, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), list[1]);
        byte[] key = new byte[32];
        Assert.True(registry.TryGetDeviceKey(B, key));
        Assert.Equal(KeyScheduleTests.Fill(32, 2), key);
    }

    [Fact]
    public void Record_with_max_devices_loads()
    {
        var entries = new byte[PairedDeviceRegistry.MaxDevices][];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = Entry(Id(i), "d", 0, 0, (byte)i);
        }

        var store = new InMemorySecretStore();
        store.Set(PairedDeviceRegistry.StoreKey, Record(entries));
        Assert.Equal(PairedDeviceRegistry.MaxDevices, new PairedDeviceRegistry(store).List().Count);

        store.Set(PairedDeviceRegistry.StoreKey, Record([.. entries, Entry(Id(entries.Length), "d", 0, 0, 0)]));
        Assert.Throws<InvalidDataException>(() => new PairedDeviceRegistry(store));
    }

    public static TheoryData<string, long, long> CorruptEntries => new()
    {
        { "long-name", 0, 0 },                // name over MaxNameBytes
        { "bad-utf8", 0, 0 },                 // name is not UTF-8
        { "ok", long.MaxValue, 0 },           // pairedAt out of range
        { "ok", 0, long.MinValue },           // lastSeen out of range
        { "ok", -62_135_596_800_001, 0 },     // one ms before 0001-01-01
        { "ok", 0, 253_402_300_800_000 },     // one ms after 9999-12-31T23:59:59.999
    };

    [Theory]
    [MemberData(nameof(CorruptEntries))]
    public void Corrupt_entry_fields_are_rejected(string name, long pairedAtMs, long lastSeenMs)
    {
        byte[] entry = name switch
        {
            "long-name" => Entry(A, new string('n', PairedDeviceRegistry.MaxNameBytes + 1), 0, 0, 1),
            "bad-utf8" => Entry(A, [0xC3, 0x28], pairedAtMs, lastSeenMs, 1),
            _ => Entry(A, name, pairedAtMs, lastSeenMs, 1),
        };
        var store = new InMemorySecretStore();
        store.Set(PairedDeviceRegistry.StoreKey, Record(entry));
        Assert.Throws<InvalidDataException>(() => new PairedDeviceRegistry(store));
    }

    [Fact]
    public void Loaded_record_bytes_are_zeroed()
    {
        var inner = new InMemorySecretStore();
        new PairedDeviceRegistry(inner).Add(A, "a", KeyScheduleTests.Fill(32, 9));
        var store = new HandOutStore(inner);

        Assert.Single(new PairedDeviceRegistry(store).List());
        Assert.NotNull(store.LastHandedOut);
        Assert.Equal(new byte[store.LastHandedOut.Length], store.LastHandedOut);
    }

    private static Guid Id(int i) => new(i, 0x1234, 0x5678, [1, 2, 3, 4, 5, 6, 7, 8]);

    private static byte[] Record(params byte[][] entries)
    {
        byte[] header = [1, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1), (ushort)entries.Length);
        return [.. header, .. entries.SelectMany(e => e)];
    }

    private static byte[] Entry(Guid id, string name, long pairedAtMs, long lastSeenMs, byte keySeed) =>
        Entry(id, Encoding.UTF8.GetBytes(name), pairedAtMs, lastSeenMs, keySeed);

    private static byte[] Entry(Guid id, byte[] name, long pairedAtMs, long lastSeenMs, byte keySeed)
    {
        byte[] e = new byte[16 + 2 + name.Length + 16 + 32];
        Assert.True(id.TryWriteBytes(e, bigEndian: true, out _));
        BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(16), (ushort)name.Length);
        name.CopyTo(e, 18);
        BinaryPrimitives.WriteInt64BigEndian(e.AsSpan(18 + name.Length), pairedAtMs);
        BinaryPrimitives.WriteInt64BigEndian(e.AsSpan(26 + name.Length), lastSeenMs);
        KeyScheduleTests.Fill(32, keySeed).CopyTo(e, 34 + name.Length);
        return e;
    }

    /// <summary>A store that remembers the last array it handed out, to observe what the caller does with it.</summary>
    internal sealed class HandOutStore(ISecretStore inner) : ISecretStore
    {
        public byte[]? LastHandedOut { get; private set; }

        public bool TryGet(string key, [NotNullWhen(true)] out byte[]? value)
        {
            bool found = inner.TryGet(key, out value);
            LastHandedOut = value;
            return found;
        }

        public void Set(string key, ReadOnlySpan<byte> value) => inner.Set(key, value);

        public void Delete(string key) => inner.Delete(key);
    }
}
