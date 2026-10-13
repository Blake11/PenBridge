using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PenBridge.Core.Security;

/// <summary>Public (non-secret) view of a paired device.</summary>
/// <param name="DeviceId">Id from Hello.</param>
/// <param name="Name">Device name (UTF-8, at most 256 bytes).</param>
/// <param name="PairedAt">When pairing completed.</param>
/// <param name="LastSeen">Last successful authentication.</param>
public sealed record PairedDevice(Guid DeviceId, string Name, DateTimeOffset PairedAt, DateTimeOffset LastSeen);

/// <summary>The Mac's paired devices (ADR-0010). Keys never appear in <see cref="List"/>.</summary>
public interface IPairedDeviceRegistry
{
    /// <summary>Snapshot of all paired devices, ordered by pairing time.</summary>
    IReadOnlyList<PairedDevice> List();

    /// <summary>Adds a device, or replaces it if the id is already paired (re-pairing).</summary>
    void Add(Guid deviceId, string name, ReadOnlySpan<byte> deviceKey);

    /// <summary>Forgets a device and its key. Returns whether it existed.</summary>
    bool Remove(Guid deviceId);

    /// <summary>Copies the device's 32-byte <c>K_dev</c>; <see langword="false"/> if unknown.</summary>
    bool TryGetDeviceKey(Guid deviceId, Span<byte> destination);

    /// <summary>Records a successful authentication now. Returns whether the device exists.</summary>
    bool Touch(Guid deviceId);
}

/// <summary>
/// <see cref="IPairedDeviceRegistry"/> persisted as one binary record in an <see cref="ISecretStore"/>.
/// </summary>
/// <remarks>
/// Record (big-endian): u8 version(1) ‖ u16 count ‖ count × { id(16, big-endian GUID) ‖ u16 nameLen ‖
/// name(UTF-8) ‖ i64 pairedAtUnixMs ‖ i64 lastSeenUnixMs ‖ K_dev(32) }. Loading rejects any
/// malformed record with <see cref="InvalidDataException"/>. Thread-safe.
/// </remarks>
public sealed class PairedDeviceRegistry : IPairedDeviceRegistry
{
    /// <summary>Secret-store key of the record.</summary>
    public const string StoreKey = "pb.paired-devices";

    /// <summary>Maximum number of paired devices.</summary>
    public const int MaxDevices = 64;

    /// <summary>Maximum UTF-8 name length (matches the wire string cap).</summary>
    public const int MaxNameBytes = 256;

    private const byte FormatVersion = 1;

    private readonly ISecretStore _store;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];

    /// <summary>Loads the registry from <paramref name="store"/>.</summary>
    /// <exception cref="InvalidDataException">The stored record is corrupt.</exception>
    public PairedDeviceRegistry(ISecretStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
        if (store.TryGet(StoreKey, out byte[]? record))
        {
            try
            {
                Decode(record, _entries);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(record);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PairedDevice> List()
    {
        lock (_lock)
        {
            var result = new PairedDevice[_entries.Count];
            for (int i = 0; i < result.Length; i++)
            {
                Entry e = _entries[i];
                result[i] = new PairedDevice(e.Id, e.Name, e.PairedAt, e.LastSeen);
            }

            return result;
        }
    }

    /// <inheritdoc />
    public void Add(Guid deviceId, string name, ReadOnlySpan<byte> deviceKey)
    {
        ArgumentNullException.ThrowIfNull(name);
        KeySchedule.RequireLength(deviceKey, KeySchedule.KeyLength, nameof(deviceKey));
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
        {
            throw new ArgumentException("Name exceeds 256 UTF-8 bytes.", nameof(name));
        }

        DateTimeOffset now = Truncate(_time.GetUtcNow());
        lock (_lock)
        {
            int index = IndexOf(deviceId);
            if (index < 0 && _entries.Count >= MaxDevices)
            {
                throw new InvalidOperationException($"At most {MaxDevices} devices can be paired.");
            }

            var entry = new Entry(deviceId, name, now, now, deviceKey.ToArray());
            if (index >= 0)
            {
                CryptographicOperations.ZeroMemory(_entries[index].Key);
                _entries.RemoveAt(index);
            }

            _entries.Add(entry);
            SaveLocked();
        }
    }

    /// <inheritdoc />
    public bool Remove(Guid deviceId)
    {
        lock (_lock)
        {
            int index = IndexOf(deviceId);
            if (index < 0)
            {
                return false;
            }

            CryptographicOperations.ZeroMemory(_entries[index].Key);
            _entries.RemoveAt(index);
            SaveLocked();
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryGetDeviceKey(Guid deviceId, Span<byte> destination)
    {
        if (destination.Length < KeySchedule.KeyLength)
        {
            throw new ArgumentException("Destination must hold 32 bytes.", nameof(destination));
        }

        lock (_lock)
        {
            int index = IndexOf(deviceId);
            if (index < 0)
            {
                return false;
            }

            _entries[index].Key.CopyTo(destination);
            return true;
        }
    }

    /// <inheritdoc />
    public bool Touch(Guid deviceId)
    {
        DateTimeOffset now = Truncate(_time.GetUtcNow());
        lock (_lock)
        {
            int index = IndexOf(deviceId);
            if (index < 0)
            {
                return false;
            }

            _entries[index] = _entries[index] with { LastSeen = now };
            SaveLocked();
            return true;
        }
    }

    private int IndexOf(Guid id)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void SaveLocked()
    {
        int size = 3;
        foreach (Entry e in _entries)
        {
            size += 16 + 2 + Encoding.UTF8.GetByteCount(e.Name) + 8 + 8 + KeySchedule.KeyLength;
        }

        byte[] buffer = new byte[size];
        try
        {
            Span<byte> span = buffer;
            span[0] = FormatVersion;
            BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)_entries.Count);
            int o = 3;
            foreach (Entry e in _entries)
            {
                e.Id.TryWriteBytes(span.Slice(o, 16), bigEndian: true, out _);
                o += 16;
                int nameLength = Encoding.UTF8.GetBytes(e.Name, span[(o + 2)..]);
                BinaryPrimitives.WriteUInt16BigEndian(span[o..], (ushort)nameLength);
                o += 2 + nameLength;
                BinaryPrimitives.WriteInt64BigEndian(span[o..], e.PairedAt.ToUnixTimeMilliseconds());
                BinaryPrimitives.WriteInt64BigEndian(span[(o + 8)..], e.LastSeen.ToUnixTimeMilliseconds());
                o += 16;
                e.Key.CopyTo(span[o..]);
                o += KeySchedule.KeyLength;
            }

            _store.Set(StoreKey, buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    internal static void Decode(ReadOnlySpan<byte> record, List<Entry> into)
    {
        if (record.Length < 3 || record[0] != FormatVersion)
        {
            throw new InvalidDataException("Unsupported paired-device record.");
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(record[1..]);
        if (count > MaxDevices)
        {
            throw new InvalidDataException("Too many paired devices.");
        }

        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        int o = 3;
        for (int i = 0; i < count; i++)
        {
            if (record.Length - o < 18)
            {
                throw new InvalidDataException("Truncated paired-device record.");
            }

            var id = new Guid(record.Slice(o, 16), bigEndian: true);
            int nameLength = BinaryPrimitives.ReadUInt16BigEndian(record[(o + 16)..]);
            o += 18;
            if (nameLength > MaxNameBytes || record.Length - o < nameLength + 16 + KeySchedule.KeyLength)
            {
                throw new InvalidDataException("Truncated paired-device record.");
            }

            string name;
            try
            {
                name = decoder.GetString(record.Slice(o, nameLength));
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Invalid device name.", ex);
            }

            o += nameLength;
            DateTimeOffset pairedAt = FromUnixMs(BinaryPrimitives.ReadInt64BigEndian(record[o..]));
            DateTimeOffset lastSeen = FromUnixMs(BinaryPrimitives.ReadInt64BigEndian(record[(o + 8)..]));
            o += 16;
            byte[] key = record.Slice(o, KeySchedule.KeyLength).ToArray();
            o += KeySchedule.KeyLength;

            foreach (Entry existing in into)
            {
                if (existing.Id == id)
                {
                    throw new InvalidDataException("Duplicate device id.");
                }
            }

            into.Add(new Entry(id, name, pairedAt, lastSeen, key));
        }

        if (o != record.Length)
        {
            throw new InvalidDataException("Trailing bytes in paired-device record.");
        }
    }

    private static DateTimeOffset FromUnixMs(long ms)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("Invalid timestamp.", ex);
        }
    }

    private static DateTimeOffset Truncate(DateTimeOffset t) => DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds());

    internal sealed record Entry(Guid Id, string Name, DateTimeOffset PairedAt, DateTimeOffset LastSeen, byte[] Key);
}
