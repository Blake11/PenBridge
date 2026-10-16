using System.Globalization;
using System.Text;
using System.Text.Unicode;
using PenBridge.Core.Input;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Protocol;

#pragma warning disable CA1720 // The names are the wire value types of the spec (Bool, Int, Float, String, Rect).

#pragma warning disable CA1028 // Wire enum: storage matches the u8 wire field.

/// <summary><c>u8</c> value type of a <see cref="SettingEntry"/> (protocol 1.2, ADR-0030).</summary>
public enum SettingType : byte
{
    /// <summary><c>u8</c> 0 or 1.</summary>
    Bool = 1,

    /// <summary><c>i32</c>.</summary>
    Int = 2,

    /// <summary><c>f32</c>, finite.</summary>
    Float = 3,

    /// <summary>UTF-8, at most <see cref="WireString.MaxBytes"/> bytes (the entry length is the byte count).</summary>
    String = 4,

    /// <summary>4 × <c>f32</c> (x, y, w, h) normalized: inside 0..1, w and h &gt; 0.</summary>
    Rect = 5,
}

#pragma warning restore CA1028

/// <summary>
/// One setting on the wire (SettingsSnapshot, SettingsUpdate): <c>u16 key ‖ u8 type ‖ u16 len ‖ value</c>.
/// The value is always structurally valid for its <see cref="Type"/> (finite floats, a normalized rect, a capped string);
/// whether it fits the key is the receiver's decision (<see cref="MacSettings.TryValidate"/>).
/// </summary>
public readonly struct SettingEntry : IEquatable<SettingEntry>
{
    /// <summary>Size of <c>key ‖ type ‖ len</c>.</summary>
    public const int HeaderSize = 2 + 1 + 2;

    /// <summary>Tolerance for a rect's far edges past 1 (float rounding of x + w).</summary>
    internal const float RectEpsilon = 1e-6f;

    private readonly int _int;
    private readonly float _x;
    private readonly float _y;
    private readonly float _w;
    private readonly float _h;
    private readonly string? _string;

    private SettingEntry(ushort key, SettingType type, int intValue, float x, float y, float w, float h, string? text)
    {
        Key = key;
        Type = type;
        _int = intValue;
        _x = x;
        _y = y;
        _w = w;
        _h = h;
        _string = text;
    }

    /// <summary>The setting key (for Mac settings, a <see cref="MacSettingKey"/>).</summary>
    public ushort Key { get; }

    public SettingType Type { get; }

    public bool BoolValue => Type == SettingType.Bool ? _int != 0 : throw WrongType(SettingType.Bool);

    public int IntValue => Type == SettingType.Int ? _int : throw WrongType(SettingType.Int);

    public float FloatValue => Type == SettingType.Float ? _x : throw WrongType(SettingType.Float);

    public string StringValue => Type == SettingType.String ? _string ?? string.Empty : throw WrongType(SettingType.String);

    /// <summary>The rect (fractions of the target); the wire carries it as <c>f32</c>.</summary>
    public NormalizedRect RectValue => Type == SettingType.Rect ? new NormalizedRect(_x, _y, _w, _h) : throw WrongType(SettingType.Rect);

    /// <summary>Encoded size: <see cref="HeaderSize"/> plus the value.</summary>
    public int Size => HeaderSize + ValueSize;

    internal int ValueSize => Type switch
    {
        SettingType.Bool => 1,
        SettingType.Int or SettingType.Float => 4,
        SettingType.String => Encoding.UTF8.GetByteCount(_string ?? string.Empty),
        SettingType.Rect => 16,
        _ => throw new InvalidOperationException("Uninitialized setting entry."),
    };

    public static SettingEntry Bool(ushort key, bool value) => new(key, SettingType.Bool, value ? 1 : 0, 0, 0, 0, 0, null);

    public static SettingEntry Bool(MacSettingKey key, bool value) => Bool((ushort)key, value);

    public static SettingEntry Int(ushort key, int value) => new(key, SettingType.Int, value, 0, 0, 0, 0, null);

    public static SettingEntry Int(MacSettingKey key, int value) => Int((ushort)key, value);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for NaN or infinity.</summary>
    public static SettingEntry Float(ushort key, float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Setting floats must be finite.");
        }

        return new(key, SettingType.Float, 0, value, 0, 0, 0, null);
    }

    public static SettingEntry Float(MacSettingKey key, float value) => Float((ushort)key, value);

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="value"/> exceeds <see cref="WireString.MaxBytes"/> UTF-8 bytes.</summary>
    public static SettingEntry String(ushort key, string value)
    {
        _ = WireString.GetSize(value, nameof(value));
        return new(key, SettingType.String, 0, 0, 0, 0, 0, value);
    }

    public static SettingEntry String(MacSettingKey key, string value) => String((ushort)key, value);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> unless the rect (as <c>f32</c>) is non-empty and inside 0..1.</summary>
    public static SettingEntry Rect(ushort key, NormalizedRect value)
    {
        float x = (float)value.X, y = (float)value.Y, w = (float)value.Width, h = (float)value.Height;
        if (!IsValidRect(x, y, w, h))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Setting rects must be non-empty and inside 0..1.");
        }

        return new(key, SettingType.Rect, 0, x, y, w, h, null);
    }

    public static SettingEntry Rect(MacSettingKey key, NormalizedRect value) => Rect((ushort)key, value);

    public bool Equals(SettingEntry other) =>
        Key == other.Key && Type == other.Type && _int == other._int
        && _x.Equals(other._x) && _y.Equals(other._y) && _w.Equals(other._w) && _h.Equals(other._h)
        && string.Equals(_string, other._string, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SettingEntry other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, Type, _int, _x, _y, _w, _h, _string);

    public static bool operator ==(SettingEntry left, SettingEntry right) => left.Equals(right);

    public static bool operator !=(SettingEntry left, SettingEntry right) => !left.Equals(right);

    /// <summary>The value as invariant text (<c>true</c>, <c>2</c>, <c>0.5</c>, the string, <c>x,y,w,h</c>).</summary>
    public string FormatValue() => Type switch
    {
        SettingType.Bool => _int != 0 ? "true" : "false",
        SettingType.Int => _int.ToString(CultureInfo.InvariantCulture),
        SettingType.Float => _x.ToString("R", CultureInfo.InvariantCulture),
        SettingType.String => _string ?? string.Empty,
        SettingType.Rect => string.Create(CultureInfo.InvariantCulture, $"{_x:R},{_y:R},{_w:R},{_h:R}"),
        _ => string.Empty,
    };

    /// <summary><c>key:type=value</c>. A string value is peer text: sanitize before showing it.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Key}:{Type}={FormatValue()}");

    internal static bool IsValidRect(float x, float y, float w, float h) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(w) && float.IsFinite(h)
        && x >= 0 && y >= 0 && w > 0 && h > 0
        && x + w <= 1 + RectEpsilon && y + h <= 1 + RectEpsilon;

    internal void Write(ref WireWriter w)
    {
        w.WriteU16(Key);
        w.WriteU8((byte)Type);
        w.WriteU16((ushort)ValueSize);
        switch (Type)
        {
            case SettingType.Bool:
                w.WriteU8((byte)_int);
                break;
            case SettingType.Int:
                w.WriteU32(unchecked((uint)_int));
                break;
            case SettingType.Float:
                w.WriteF32(_x);
                break;
            case SettingType.String:
                w.WriteBytes(Encoding.UTF8.GetBytes(_string ?? string.Empty));
                break;
            case SettingType.Rect:
                w.WriteF32(_x);
                w.WriteF32(_y);
                w.WriteF32(_w);
                w.WriteF32(_h);
                break;
            default:
                throw new InvalidOperationException("Uninitialized setting entry.");
        }
    }

    /// <summary>
    /// Reads one entry. False only on truncation (a protocol error). An entry of unknown type, or whose length or
    /// value doesn't fit its type, is consumed and reported with <paramref name="valid"/> = false (skipped).
    /// </summary>
    internal static bool TryRead(ref WireReader r, out SettingEntry entry, out bool valid)
    {
        entry = default;
        valid = false;
        if (!r.TryReadU16(out ushort key) || !r.TryReadU8(out byte type) || !r.TryReadU16(out ushort length)
            || !r.TryReadSpan(length, out ReadOnlySpan<byte> value))
        {
            return false;
        }

        valid = TryDecode(key, (SettingType)type, value, out entry);
        return true;
    }

    private static bool TryDecode(ushort key, SettingType type, ReadOnlySpan<byte> value, out SettingEntry entry)
    {
        entry = default;
        var r = new WireReader(value);
        switch (type)
        {
            case SettingType.Bool when value.Length == 1 && value[0] <= 1:
                entry = Bool(key, value[0] == 1);
                return true;
            case SettingType.Int when value.Length == 4:
                r.TryReadU32(out uint raw);
                entry = Int(key, unchecked((int)raw));
                return true;
            case SettingType.Float when value.Length == 4:
                r.TryReadF32(out float f);
                if (!float.IsFinite(f))
                {
                    return false;
                }

                entry = new(key, SettingType.Float, 0, f, 0, 0, 0, null);
                return true;
            case SettingType.String when value.Length <= WireString.MaxBytes && Utf8.IsValid(value):
                entry = new(key, SettingType.String, 0, 0, 0, 0, 0, Encoding.UTF8.GetString(value));
                return true;
            case SettingType.Rect when value.Length == 16:
                r.TryReadF32(out float x);
                r.TryReadF32(out float y);
                r.TryReadF32(out float w);
                r.TryReadF32(out float h);
                if (!IsValidRect(x, y, w, h))
                {
                    return false;
                }

                entry = new(key, SettingType.Rect, 0, x, y, w, h, null);
                return true;
            default:
                return false;
        }
    }

    private InvalidOperationException WrongType(SettingType wanted) =>
        new($"Setting {Key} is a {Type}, not a {wanted}.");
}
