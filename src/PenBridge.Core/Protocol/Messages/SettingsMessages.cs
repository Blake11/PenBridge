using PenBridge.Core.Input;

namespace PenBridge.Core.Protocol;

/// <summary>Shared rules of the protocol 1.2 settings messages (ADR-0030, ADR-0031).</summary>
public static class SettingsWire
{
    /// <summary>Most entries in a SettingsSnapshot or SettingsUpdate; a larger count is a protocol error.</summary>
    public const int MaxEntries = 64;

    /// <summary>Most displays in a DisplayList; a larger count is a protocol error.</summary>
    public const int MaxDisplays = 16;

    /// <summary>Copies a list of entries, checking the count and that every entry is initialized.</summary>
    internal static SettingEntry[] CopyEntries(IReadOnlyList<SettingEntry> entries, int min, string paramName)
    {
        ArgumentNullException.ThrowIfNull(entries, paramName);
        if (entries.Count < min || entries.Count > MaxEntries)
        {
            throw new ArgumentException($"Expected {min}..{MaxEntries} setting entries.", paramName);
        }

        var copy = new SettingEntry[entries.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = entries[i];
            if (copy[i].Type is < SettingType.Bool or > SettingType.Rect)
            {
                throw new ArgumentException("Uninitialized setting entry.", paramName);
            }
        }

        return copy;
    }

    internal static int EntriesSize(SettingEntry[]? entries)
    {
        int size = 1;
        foreach (SettingEntry entry in entries ?? [])
        {
            size += entry.Size;
        }

        return size;
    }

    internal static int WriteEntries(Span<byte> destination, SettingEntry[]? entries)
    {
        var w = new WireWriter(destination);
        entries ??= [];
        w.WriteU8((byte)entries.Length);
        foreach (SettingEntry entry in entries)
        {
            entry.Write(ref w);
        }

        return w.Written;
    }

    /// <summary>
    /// Reads <c>u8 count ‖ entries</c>. False on truncation or a count outside <paramref name="min"/>..<see cref="MaxEntries"/>;
    /// entries of unknown type or with a value that doesn't fit their type are skipped.
    /// </summary>
    internal static bool TryReadEntries(ReadOnlySpan<byte> payload, int min, out SettingEntry[] entries)
    {
        entries = [];
        var r = new WireReader(payload);
        if (!r.TryReadU8(out byte count) || count < min || count > MaxEntries)
        {
            return false;
        }

        var list = new SettingEntry[count];
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            if (!SettingEntry.TryRead(ref r, out SettingEntry entry, out bool valid))
            {
                return false;
            }

            if (valid)
            {
                list[kept++] = entry;
            }
        }

        entries = kept == list.Length ? list : list[..kept];
        return true;
    }
}

/// <summary>
/// 0x40 S → C (needs <see cref="ProtocolCapabilities.RemoteSettings"/>). Every Mac setting, sent after SessionStart,
/// after each change and in reply to every SettingsUpdate. Payload: <c>u8 count (≤ 64) ‖ entries</c>. Entries a reader
/// can't decode are skipped; their keys are up to the receiver (unknown keys are ignored there).
/// </summary>
public readonly struct SettingsSnapshotMessage : IControlMessage<SettingsSnapshotMessage>
{
    private readonly SettingEntry[]? _entries;

    /// <summary>Throws <see cref="ArgumentException"/> for more than <see cref="SettingsWire.MaxEntries"/> entries.</summary>
    public SettingsSnapshotMessage(IReadOnlyList<SettingEntry> entries) =>
        _entries = SettingsWire.CopyEntries(entries, 0, nameof(entries));

    private SettingsSnapshotMessage(SettingEntry[] entries, bool _) => _entries = entries;

    public static ControlMessageType Type => ControlMessageType.SettingsSnapshot;

    public IReadOnlyList<SettingEntry> Entries => _entries ?? [];

    public int PayloadSize => SettingsWire.EntriesSize(_entries);

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        return SettingsWire.WriteEntries(destination, _entries);
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out SettingsSnapshotMessage message)
    {
        bool ok = SettingsWire.TryReadEntries(payload, 0, out SettingEntry[] entries);
        message = ok ? new SettingsSnapshotMessage(entries, true) : default;
        return ok;
    }
}

/// <summary>
/// 0x41 C → S (needs <see cref="ProtocolCapabilities.RemoteSettings"/>). Settings the phone wants changed; the Mac
/// applies the valid ones and answers with a SettingsSnapshot. Payload: <c>u8 count (1..64) ‖ entries</c>.
/// </summary>
public readonly struct SettingsUpdateMessage : IControlMessage<SettingsUpdateMessage>
{
    private readonly SettingEntry[]? _entries;

    /// <summary>Throws <see cref="ArgumentException"/> unless there are 1..<see cref="SettingsWire.MaxEntries"/> entries.</summary>
    public SettingsUpdateMessage(IReadOnlyList<SettingEntry> entries) =>
        _entries = SettingsWire.CopyEntries(entries, 1, nameof(entries));

    private SettingsUpdateMessage(SettingEntry[] entries, bool _) => _entries = entries;

    public static ControlMessageType Type => ControlMessageType.SettingsUpdate;

    /// <summary>The decodable entries (possibly fewer than the wire count, possibly none).</summary>
    public IReadOnlyList<SettingEntry> Entries => _entries ?? [];

    public int PayloadSize => _entries is null
        ? throw new InvalidOperationException("A SettingsUpdate needs at least one entry.")
        : SettingsWire.EntriesSize(_entries);

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        return SettingsWire.WriteEntries(destination, _entries);
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out SettingsUpdateMessage message)
    {
        bool ok = SettingsWire.TryReadEntries(payload, 1, out SettingEntry[] entries);
        message = ok ? new SettingsUpdateMessage(entries, true) : default;
        return ok;
    }
}

/// <summary>
/// One display the user can target, in a DisplayList. <see cref="Id"/> is the Mac's stable display id (its UUID),
/// never empty and never <c>"all"</c> (both are reserved TargetDisplay values). Strings are peer text on the phone.
/// </summary>
public sealed record DisplayListEntry
{
    /// <summary>Throws <see cref="ArgumentException"/> for an empty or reserved id, a string over 256 UTF-8 bytes, or a size that isn't finite and positive.</summary>
    public DisplayListEntry(string id, string name, bool isMain, float widthPt, float heightPt)
    {
        _ = WireString.GetSize(id, nameof(id));
        _ = WireString.GetSize(name, nameof(name));
        if (!IsValid(id, widthPt, heightPt))
        {
            throw new ArgumentException("Display ids must be non-empty and not reserved; sizes finite and positive.");
        }

        Id = id;
        Name = name;
        IsMain = isMain;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsMain { get; }

    /// <summary>Width in points (for the phone's aspect preview).</summary>
    public float WidthPt { get; }

    public float HeightPt { get; }

    internal int Size => WireString.GetSize(Id, nameof(Id)) + WireString.GetSize(Name, nameof(Name)) + 1 + 4 + 4;

    internal static bool IsValid(string id, float widthPt, float heightPt) =>
        id.Length > 0 && id != Settings.MacSettings.AllDisplays
        && float.IsFinite(widthPt) && widthPt > 0 && float.IsFinite(heightPt) && heightPt > 0;
}

/// <summary>
/// 0x42 S → C (needs <see cref="ProtocolCapabilities.RemoteSettings"/>). The displays the user can pick; "All displays"
/// is implied and not listed. Sent with the first snapshot and on display changes. Payload: <c>u8 count (≤ 16) ‖
/// { string id ‖ string name ‖ u8 isMain ‖ f32 widthPt ‖ f32 heightPt }</c>. Any invalid entry is a protocol error.
/// </summary>
public readonly struct DisplayListMessage : IControlMessage<DisplayListMessage>
{
    private readonly DisplayListEntry[]? _displays;

    /// <summary>Throws <see cref="ArgumentException"/> for more than <see cref="SettingsWire.MaxDisplays"/> displays.</summary>
    public DisplayListMessage(IReadOnlyList<DisplayListEntry> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count > SettingsWire.MaxDisplays)
        {
            throw new ArgumentException($"At most {SettingsWire.MaxDisplays} displays.", nameof(displays));
        }

        var copy = new DisplayListEntry[displays.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = displays[i] ?? throw new ArgumentException("Null display.", nameof(displays));
        }

        _displays = copy;
    }

    private DisplayListMessage(DisplayListEntry[] displays, bool _) => _displays = displays;

    public static ControlMessageType Type => ControlMessageType.DisplayList;

    public IReadOnlyList<DisplayListEntry> Displays => _displays ?? [];

    public int PayloadSize
    {
        get
        {
            int size = 1;
            foreach (DisplayListEntry display in _displays ?? [])
            {
                size += display.Size;
            }

            return size;
        }
    }

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        DisplayListEntry[] displays = _displays ?? [];
        w.WriteU8((byte)displays.Length);
        foreach (DisplayListEntry display in displays)
        {
            w.WriteString(display.Id);
            w.WriteString(display.Name);
            w.WriteU8(display.IsMain ? (byte)1 : (byte)0);
            w.WriteF32(display.WidthPt);
            w.WriteF32(display.HeightPt);
        }

        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out DisplayListMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU8(out byte count) || count > SettingsWire.MaxDisplays)
        {
            return false;
        }

        var displays = new DisplayListEntry[count];
        for (int i = 0; i < count; i++)
        {
            if (!r.TryReadString(out string id) || !r.TryReadString(out string name)
                || !r.TryReadU8(out byte isMain) || isMain > 1
                || !r.TryReadF32(out float width) || !r.TryReadF32(out float height)
                || !DisplayListEntry.IsValid(id, width, height))
            {
                return false;
            }

            displays[i] = new DisplayListEntry(id, name, isMain == 1, width, height);
        }

        message = new DisplayListMessage(displays, true);
        return true;
    }
}

/// <summary>
/// 0x43 S → C (needs <see cref="ProtocolCapabilities.ActiveArea"/>, ADR-0031). The part of the phone surface that maps
/// onto the whole target, in the current surface orientation on the 0..65535 scale. Payload: <c>u16 x ‖ u16 y ‖ u16 w ‖
/// u16 h</c> with w, h &gt; 0 and x + w, y + h ≤ 65535.
/// </summary>
public readonly struct ActiveAreaMessage : IControlMessage<ActiveAreaMessage>
{
    /// <summary>The largest coordinate.</summary>
    public const ushort Max = ushort.MaxValue;

    public const int Size = 8;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for an empty area or one past the surface.</summary>
    public ActiveAreaMessage(ushort x, ushort y, ushort width, ushort height)
    {
        if (!IsValid(x, y, width, height))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The active area must be non-empty and inside 0..65535.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static ControlMessageType Type => ControlMessageType.ActiveArea;

    /// <summary>The whole surface.</summary>
    public static ActiveAreaMessage Full => new(0, 0, Max, Max);

    public ushort X { get; }

    public ushort Y { get; }

    public ushort Width { get; }

    public ushort Height { get; }

    public int PayloadSize => IsValid(X, Y, Width, Height) ? Size : throw new InvalidOperationException("Invalid active area.");

    /// <summary>The area as fractions of the surface.</summary>
    public NormalizedRect ToNormalizedRect() => new(X / (double)Max, Y / (double)Max, Width / (double)Max, Height / (double)Max);

    /// <summary>Rounds a normalized rect onto the wire scale, clamped inside the surface and at least one unit wide and high.</summary>
    public static ActiveAreaMessage FromNormalized(NormalizedRect area)
    {
        if (!double.IsFinite(area.X) || !double.IsFinite(area.Y) || !double.IsFinite(area.Width) || !double.IsFinite(area.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(area), "The active area must be finite.");
        }

        (ushort x, ushort w) = Axis(area.X, area.Width);
        (ushort y, ushort h) = Axis(area.Y, area.Height);
        return new ActiveAreaMessage(x, y, w, h);

        static (ushort Start, ushort Length) Axis(double start, double length)
        {
            int s = (int)Math.Clamp(Math.Round(start * Max), 0, Max - 1);
            int e = (int)Math.Clamp(Math.Round((start + length) * Max), s + 1, Max);
            return ((ushort)s, (ushort)(e - s));
        }
    }

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU16(X);
        w.WriteU16(Y);
        w.WriteU16(Width);
        w.WriteU16(Height);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ActiveAreaMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU16(out ushort x) || !r.TryReadU16(out ushort y) || !r.TryReadU16(out ushort w) || !r.TryReadU16(out ushort h)
            || !IsValid(x, y, w, h))
        {
            return false;
        }

        message = new ActiveAreaMessage(x, y, w, h);
        return true;
    }

    private static bool IsValid(ushort x, ushort y, ushort width, ushort height) =>
        width > 0 && height > 0 && x + width <= Max && y + height <= Max;
}
