using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;
using static PenBridge.Core.Tests.Protocol.ControlMessageGoldenTests;

namespace PenBridge.Core.Tests.Protocol;

/// <summary>Protocol 1.2 (ADR-0030, ADR-0031): SettingsSnapshot, SettingsUpdate, DisplayList, ActiveArea.</summary>
public class SettingsMessageTests
{
    private const string BoolZoom = "0017 01 0001 01";
    private const string IntStylus = "000A 02 0004 00000001";
    private const string FloatMouse = "000B 03 0004 3FC00000";
    private const string StringTarget = "0001 04 0002 6162";
    private const string RectRegion = "0002 05 0010 3E800000 3F000000 3F000000 3E800000";

    private static readonly SettingEntry[] AllTypes =
    [
        SettingEntry.Bool(MacSettingKey.Zoom, true),
        SettingEntry.Int(MacSettingKey.StylusMode, 1),
        SettingEntry.Float(MacSettingKey.MouseSpeed, 1.5f),
        SettingEntry.String(MacSettingKey.TargetDisplay, "ab"),
        SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(0.25, 0.5, 0.5, 0.25)),
    ];

    [Fact]
    public void Codes_are_stable()
    {
        Assert.Equal(4u, (uint)ProtocolCapabilities.RemoteSettings);
        Assert.Equal(8u, (uint)ProtocolCapabilities.ActiveArea);
        Assert.Equal(0x40, (int)ControlMessageType.SettingsSnapshot);
        Assert.Equal(0x41, (int)ControlMessageType.SettingsUpdate);
        Assert.Equal(0x42, (int)ControlMessageType.DisplayList);
        Assert.Equal(0x43, (int)ControlMessageType.ActiveArea);
        Assert.Equal([1, 2, 3, 4, 5], Enum.GetValues<SettingType>().Select(t => (int)t));
        Assert.Equal(new ProtocolVersion(1, 3), ProtocolVersion.Current);
        for (byte t = 0x40; t <= 0x43; t++)
        {
            Assert.True(ControlMessageTypes.IsKnown(t));
        }

        Assert.False(ControlMessageTypes.IsKnown(0x44));
    }

    [Fact]
    public void SettingsSnapshot_with_every_type()
    {
        SettingsSnapshotMessage back = Golden(
            new SettingsSnapshotMessage(AllTypes),
            $"00000036 40 05 {BoolZoom} {IntStylus} {FloatMouse} {StringTarget} {RectRegion}");
        Assert.Equal(AllTypes, back.Entries);
        Assert.True(back.Entries[0].BoolValue);
        Assert.Equal(1, back.Entries[1].IntValue);
        Assert.Equal(1.5f, back.Entries[2].FloatValue);
        Assert.Equal("ab", back.Entries[3].StringValue);
        Assert.Equal(new NormalizedRect(0.25, 0.5, 0.5, 0.25), back.Entries[4].RectValue);
    }

    [Fact]
    public void Empty_snapshot() => Assert.Empty(Golden(new SettingsSnapshotMessage([]), "00000002 40 00").Entries);

    [Fact]
    public void SettingsUpdate() =>
        Assert.Equal(1, Assert.Single(Golden(new SettingsUpdateMessage([AllTypes[1]]), $"0000000B 41 01 {IntStylus}").Entries).IntValue);

    [Fact]
    public void Negative_ints_and_empty_strings_round_trip()
    {
        SettingsSnapshotMessage back = Golden(
            new SettingsSnapshotMessage([SettingEntry.Int(9, -2), SettingEntry.String(1, string.Empty)]),
            "00000010 40 02 0009 02 0004 FFFFFFFE 0001 04 0000");
        Assert.Equal(-2, back.Entries[0].IntValue);
        Assert.Equal(string.Empty, back.Entries[1].StringValue);
    }

    [Fact]
    public void DisplayList()
    {
        DisplayListMessage back = Golden(
            new DisplayListMessage([new DisplayListEntry("d1", "Mac", true, 1512, 982)]),
            "00000014 42 01 0002 6431 0003 4D6163 01 44BD0000 44758000");
        DisplayListEntry d = Assert.Single(back.Displays);
        Assert.Equal(new DisplayListEntry("d1", "Mac", true, 1512, 982), d);
        Assert.Empty(Golden(new DisplayListMessage([]), "00000002 42 00").Displays);
    }

    [Fact]
    public void ActiveArea()
    {
        ActiveAreaMessage back = Golden(new ActiveAreaMessage(0x1000, 0, 0x2000, 0xFFFF), "00000009 43 1000 0000 2000 FFFF");
        Assert.Equal((ushort)0x2000, back.Width);
        Assert.Equal(new NormalizedRect(0, 0, 1, 1), ActiveAreaMessage.Full.ToNormalizedRect());
    }

    [Theory]
    [InlineData("0000 0000 0000 0001")] // empty width
    [InlineData("0000 0000 0001 0000")] // empty height
    [InlineData("0001 0000 FFFF 0001")] // x + w past the surface
    [InlineData("0000 8000 0001 8000")] // y + h past the surface
    public void Invalid_active_areas_are_rejected(string payload) =>
        Assert.False(ActiveAreaMessage.TryRead(Hex.Bytes(payload), out _));

    [Fact]
    public void Active_area_from_normalized_rounds_and_clamps()
    {
        ActiveAreaMessage a = ActiveAreaMessage.FromNormalized(new NormalizedRect(0.125, 0, 0.75, 1));
        Assert.Equal((ushort)8192, a.X);
        Assert.Equal((ushort)49151, a.Width);
        Assert.Equal((ushort)65535, a.Height);
        ActiveAreaMessage clamped = ActiveAreaMessage.FromNormalized(new NormalizedRect(-1, 1.5, 0, 3));
        Assert.Equal((ushort)0, clamped.X);
        Assert.Equal((ushort)1, clamped.Width);
        Assert.Equal((ushort)65534, clamped.Y);
        Assert.Equal((ushort)1, clamped.Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => ActiveAreaMessage.FromNormalized(new NormalizedRect(double.NaN, 0, 1, 1)));
    }

    [Theory]
    [InlineData("0017 09 0001 01")] // unknown type
    [InlineData("0017 01 0002 0100")] // bool with a wrong length
    [InlineData("0017 01 0001 02")] // bool other than 0/1
    [InlineData("000A 02 0002 0001")] // int with a wrong length
    [InlineData("000B 03 0004 7FC00000")] // NaN
    [InlineData("000B 03 0004 7F800000")] // infinity
    [InlineData("0001 04 0002 C328")] // invalid UTF-8
    [InlineData("0002 05 0010 3F000000 00000000 3F400000 3F800000")] // x + w > 1
    [InlineData("0002 05 0010 00000000 00000000 00000000 3F800000")] // empty width
    [InlineData("0002 05 0010 BF800000 00000000 3F000000 3F800000")] // negative x
    [InlineData("0002 05 000C 00000000 00000000 3F800000")] // rect with a wrong length
    public void An_entry_that_doesnt_fit_its_type_is_skipped_not_fatal(string bad)
    {
        byte[] payload = Hex.Bytes($"03 {BoolZoom} {bad} {IntStylus}");
        Assert.True(SettingsSnapshotMessage.TryRead(payload, out SettingsSnapshotMessage snapshot));
        Assert.Equal([AllTypes[0], AllTypes[1]], snapshot.Entries);
        Assert.True(SettingsUpdateMessage.TryRead(payload, out SettingsUpdateMessage update));
        Assert.Equal([AllTypes[0], AllTypes[1]], update.Entries);
    }

    [Fact]
    public void A_string_over_256_bytes_is_skipped()
    {
        string payload = "02 0001 04 0101 " + new string('6', 2 * 257) + " " + BoolZoom;
        Assert.True(SettingsSnapshotMessage.TryRead(Hex.Bytes(payload), out SettingsSnapshotMessage snapshot));
        Assert.Equal([AllTypes[0]], snapshot.Entries);
    }

    [Fact]
    public void Unknown_keys_pass_the_wire_layer_for_the_receiver_to_ignore()
    {
        Assert.True(SettingsUpdateMessage.TryRead(Hex.Bytes("01 7777 01 0001 01"), out SettingsUpdateMessage update));
        SettingEntry entry = Assert.Single(update.Entries);
        Assert.Equal(0x7777, entry.Key);
        Assert.False(MacSettings.TryValidate(entry, new MacSettingValues(), [], out _));
    }

    [Fact]
    public void Counts_are_capped()
    {
        var max = Enumerable.Repeat(AllTypes[0], SettingsWire.MaxEntries).ToArray();
        Assert.Equal(64, new SettingsSnapshotMessage(max).Entries.Count);
        Assert.Throws<ArgumentException>(() => new SettingsSnapshotMessage([.. max, AllTypes[0]]));
        Assert.Throws<ArgumentException>(() => new SettingsUpdateMessage([.. max, AllTypes[0]]));
        Assert.Throws<ArgumentException>(() => new SettingsUpdateMessage([]));
        Assert.Throws<ArgumentException>(() => new SettingsSnapshotMessage([default]));

        // 65 entries on the wire: a protocol error, even when every entry is present.
        byte[] payload = Hex.Bytes("41" + string.Concat(Enumerable.Repeat(BoolZoom.Replace(" ", string.Empty, StringComparison.Ordinal), 65)));
        Assert.False(SettingsSnapshotMessage.TryRead(payload, out _));
        Assert.False(SettingsUpdateMessage.TryRead(payload, out _));
        payload[0] = 64;
        Assert.True(SettingsSnapshotMessage.TryRead(payload, out SettingsSnapshotMessage snapshot)); // trailing bytes ignored
        Assert.Equal(64, snapshot.Entries.Count);

        Assert.False(SettingsUpdateMessage.TryRead(Hex.Bytes("00"), out _)); // an update needs an entry
        Assert.True(SettingsSnapshotMessage.TryRead(Hex.Bytes("00"), out _));

        var displays = Enumerable.Range(0, 17).Select(i => new DisplayListEntry("d" + i, "D", false, 1, 1)).ToArray();
        Assert.Throws<ArgumentException>(() => new DisplayListMessage(displays));
        Assert.Equal(16, new DisplayListMessage(displays[..16]).Displays.Count);
        Assert.False(DisplayListMessage.TryRead(Hex.Bytes("11"), out _));
    }

    [Theory]
    [InlineData("01 0000 0003 4D6163 01 44BD0000 44758000")] // empty id
    [InlineData("01 0003 616C6C 0003 4D6163 01 44BD0000 44758000")] // "all" is reserved
    [InlineData("01 0002 6431 0003 4D6163 02 44BD0000 44758000")] // isMain other than 0/1
    [InlineData("01 0002 6431 0003 4D6163 01 00000000 44758000")] // zero width
    [InlineData("01 0002 6431 0003 4D6163 01 44BD0000 7FC00000")] // NaN height
    [InlineData("02 0002 6431 0003 4D6163 01 44BD0000 44758000")] // count above the entries present
    public void Invalid_display_lists_are_rejected(string payload) =>
        Assert.False(DisplayListMessage.TryRead(Hex.Bytes(payload), out _));

    [Fact]
    public void Writers_reject_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingEntry.Float(11, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingEntry.Rect(2, new NormalizedRect(0.5, 0, 0.6, 1)));
        Assert.Throws<ArgumentException>(() => SettingEntry.String(1, new string('x', 257)));
        Assert.Throws<ArgumentException>(() => new DisplayListEntry(string.Empty, "x", false, 1, 1));
        Assert.Throws<ArgumentException>(() => new DisplayListEntry("all", "x", false, 1, 1));
        Assert.Throws<ArgumentException>(() => new DisplayListEntry("d", "x", false, float.PositiveInfinity, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActiveAreaMessage(1, 0, ushort.MaxValue, 1));
        Assert.Throws<InvalidOperationException>(() => SettingEntry.Bool(1, true).IntValue);
        Assert.Throws<InvalidOperationException>(() => default(SettingsUpdateMessage).PayloadSize);
    }

    [Fact]
    public void Entries_compare_by_value()
    {
        Assert.Equal(SettingEntry.Float(11, 2f), SettingEntry.Float(11, 2f));
        Assert.NotEqual(SettingEntry.Float(11, 2f), SettingEntry.Float(12, 2f));
        Assert.NotEqual(SettingEntry.Int(11, 1), SettingEntry.Bool(11, true));
        Assert.Equal("11:Float=2", SettingEntry.Float(11, 2f).ToString());
        Assert.Equal("0.25,0.5,0.5,0.25", AllTypes[4].FormatValue());
    }

    [Theory]
    [InlineData(SettingType.Int)]
    [InlineData(SettingType.Float)]
    [InlineData(SettingType.String)]
    [InlineData(SettingType.Rect)]
    public void Bool_value_of_another_type_throws(SettingType type) =>
        Assert.Throws<InvalidOperationException>(() => Entry(type).BoolValue);

    [Theory]
    [InlineData(SettingType.Bool)]
    [InlineData(SettingType.Int)]
    [InlineData(SettingType.String)]
    [InlineData(SettingType.Rect)]
    public void Float_value_of_another_type_throws(SettingType type) =>
        Assert.Throws<InvalidOperationException>(() => Entry(type).FloatValue);

    [Theory]
    [InlineData(SettingType.Bool)]
    [InlineData(SettingType.Float)]
    [InlineData(SettingType.String)]
    [InlineData(SettingType.Rect)]
    public void Int_value_of_another_type_throws(SettingType type) =>
        Assert.Throws<InvalidOperationException>(() => Entry(type).IntValue);

    [Theory]
    [InlineData(SettingType.Bool)]
    [InlineData(SettingType.Int)]
    [InlineData(SettingType.Float)]
    [InlineData(SettingType.Rect)]
    public void String_value_of_another_type_throws(SettingType type) =>
        Assert.Throws<InvalidOperationException>(() => Entry(type).StringValue);

    [Theory]
    [InlineData(SettingType.Bool)]
    [InlineData(SettingType.Int)]
    [InlineData(SettingType.Float)]
    [InlineData(SettingType.String)]
    public void Rect_value_of_another_type_throws(SettingType type) =>
        Assert.Throws<InvalidOperationException>(() => Entry(type).RectValue);

    // The far edges may pass 1 by at most RectEpsilon (as f32), inclusive, on both axes.
    [Theory]
    [InlineData(0, 0, 1, 1, true)]
    [InlineData(0, 0, 1 + SettingEntry.RectEpsilon, 1, true)]
    [InlineData(0, 0, 1, 1 + SettingEntry.RectEpsilon, true)]
    [InlineData(0, 0, 1.0000012f, 1, false)]
    [InlineData(0, 0, 1, 1.0000012f, false)]
    [InlineData(0, 0.5f, 1, 0.5f, true)]
    [InlineData(0, 0.5f, 1, 0.5000012f, false)]
    [InlineData(0, 0, 1, 0, false)] // empty height
    [InlineData(0, -0.5f, 1, 1, false)] // negative y
    public void Rect_far_edges_allow_exactly_the_epsilon(float x, float y, float w, float h, bool valid)
    {
        Assert.Equal(valid, SettingEntry.IsValidRect(x, y, w, h));
        byte[] payload = Hex.Bytes($"01 0002 05 0010 {F32(x)} {F32(y)} {F32(w)} {F32(h)}");
        Assert.True(SettingsSnapshotMessage.TryRead(payload, out SettingsSnapshotMessage snapshot));
        Assert.Equal(valid ? 1 : 0, snapshot.Entries.Count);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, double.NaN, 1, 1)]
    [InlineData(0, 0, double.NaN, 1)]
    [InlineData(0, 0, 1, double.NaN)]
    [InlineData(double.NegativeInfinity, 0, 1, 1)]
    [InlineData(0, double.PositiveInfinity, 1, 1)]
    [InlineData(0, 0, double.PositiveInfinity, 1)]
    [InlineData(0, 0, 1, double.NegativeInfinity)]
    public void Active_area_from_normalized_rejects_any_non_finite_component(double x, double y, double w, double h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ActiveAreaMessage.FromNormalized(new NormalizedRect(x, y, w, h)));

    [Fact]
    public void A_failed_read_leaves_the_message_default()
    {
        Assert.False(SettingsUpdateMessage.TryRead(Hex.Bytes("01 0017 01"), out SettingsUpdateMessage update));
        Assert.Empty(update.Entries);
        Assert.Throws<InvalidOperationException>(() => update.PayloadSize);
    }

    private static SettingEntry Entry(SettingType type) => AllTypes[(int)type - 1];

    private static string F32(float value) => BitConverter.SingleToUInt32Bits(value).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}
