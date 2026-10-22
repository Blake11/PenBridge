using System.Globalization;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Protocol;

/// <summary>
/// Settings text and discovery texts don't depend on the current culture: de-DE writes decimals with a comma, and
/// tr-TR upper-cases <c>i</c> to a dotted <c>İ</c> (so culture-sensitive case-insensitive matching of names like
/// <c>tipfeel</c> or <c>middleclick</c> breaks).
/// </summary>
public class CultureInvarianceTests
{
    public static readonly TheoryData<string> Cultures = ["de-DE", "tr-TR"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Settings_entries_format_with_invariant_text(string culture) => InCulture(culture, () =>
    {
        SettingEntry region = SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(0.25, 0.5, 0.5, 0.25));
        Assert.Equal("0.25,0.5,0.5,0.25", region.FormatValue());
        Assert.Equal("Region=0.25,0.5,0.5,0.25", MacSettings.Format(region));
        Assert.Equal("2:Rect=0.25,0.5,0.5,0.25", region.ToString());
        Assert.Equal("TipFeel=-0.5", MacSettings.Format(SettingEntry.Float(MacSettingKey.TipFeel, -0.5f)));
        Assert.Equal("11:Float=1234567.9", SettingEntry.Float(11, 1234567.9f).ToString());
        Assert.Equal("-1000000", SettingEntry.Int(500, -1_000_000).FormatValue());
        Assert.Equal("RegionTint=true", MacSettings.Format(SettingEntry.Bool(MacSettingKey.RegionTint, true)));
        Assert.Equal("SideButton=MiddleClick", MacSettings.Format(SettingEntry.Int(MacSettingKey.SideButton, 1)));
    });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Settings_assignments_parse_with_invariant_numbers_and_ordinal_names(string culture) => InCulture(culture, () =>
    {
        Assert.True(MacSettings.TryParseAssignment("region=0.25,0.5,0.5,0.25", out SettingEntry region));
        Assert.Equal(new NormalizedRect(0.25, 0.5, 0.5, 0.25), region.RectValue);
        Assert.True(MacSettings.TryParseAssignment("tipfeel=-0.5", out SettingEntry tip));
        Assert.Equal(-0.5f, tip.FloatValue);
        Assert.True(MacSettings.TryParseAssignment("clickthreshold=1e-1", out SettingEntry click));
        Assert.Equal(0.1f, click.FloatValue);
        Assert.True(MacSettings.TryParseAssignment("regiontint=yes", out SettingEntry tint));
        Assert.True(tint.BoolValue);
        Assert.True(MacSettings.TryParseAssignment("MOUSEACCELERATION=off", out SettingEntry accel));
        Assert.False(accel.BoolValue);
        Assert.True(MacSettings.TryParseAssignment("sidebutton=middleclick", out SettingEntry side));
        Assert.Equal(1, side.IntValue);
        Assert.True(MacSettings.TryParseAssignment("SmartZoom=FOLLOWSYSTEM", out SettingEntry zoom));
        Assert.Equal(0, zoom.IntValue);

        // The culture's own decimal and group separators are not numbers here.
        Assert.False(MacSettings.TryParseAssignment("TipFeel=0,5", out _));
        Assert.False(MacSettings.TryParseAssignment("Region=0,25;0,5;0,5;0,25", out _));
    });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Every_setting_and_random_values_round_trip_through_text(string culture) => InCulture(culture, () =>
    {
        foreach (MacSettingDescriptor d in MacSettings.All)
        {
            AssertRoundTrip(d.Default);
        }

        var random = new Random(0xC0FFEE);
        for (int i = 0; i < 500; i++)
        {
            float x = (float)(random.NextDouble() * 0.5);
            float y = (float)(random.NextDouble() * 0.5);
            AssertRoundTrip(SettingEntry.Float(MacSettingKey.MouseSpeed, (float)((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-8, 8)))));
            AssertRoundTrip(SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(x, y, (float)(0.05 + (random.NextDouble() * 0.45)), (float)(0.05 + (random.NextDouble() * 0.45)))));
            AssertRoundTrip(SettingEntry.Int(MacSettingKey.StylusMode, random.Next(int.MinValue, int.MaxValue)));
        }
    });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Discovery_texts_are_invariant(string culture) => InCulture(culture, () =>
    {
        byte[] fingerprint = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
        byte[] token = [.. Enumerable.Range(100, 32).Select(i => (byte)i)];
        var version = new ProtocolVersion(1, 12);
        Assert.Equal("1.12", version.ToString());
        Assert.True(ProtocolVersion.TryParse("1.12", out ProtocolVersion parsed));
        Assert.Equal(version, parsed);

        var qr = new PairingQrPayload("192.168.1.20", 52_000, fingerprint, token, version);
        string text = qr.Format();
        Assert.Contains("&p=52000&", text, StringComparison.Ordinal);
        Assert.EndsWith("&v=1.12", text, StringComparison.Ordinal);
        Assert.True(PairingQrPayload.TryParse(text, out PairingQrPayload? back));
        Assert.Equal(52_000, back!.Port);
        Assert.Equal(version, back.Version);

        var txt = new BonjourTxtRecord(version, fingerprint, "İstanbul Mac");
        Assert.True(BonjourTxtRecord.TryParse(txt.ToEntries(), out BonjourTxtRecord? record));
        Assert.Equal(version, record!.Version);
        Assert.Equal("İstanbul Mac", record.Name);
    });

    private static void AssertRoundTrip(SettingEntry entry)
    {
        string text = MacSettings.Format(entry);
        Assert.True(MacSettings.TryParseAssignment(text, out SettingEntry back), $"'{text}' did not parse");
        Assert.Equal(entry, back);
    }

    private static void InCulture(string name, Action test)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            Assert.Equal(name, CultureInfo.CurrentCulture.Name);
            Assert.Equal("0,5", 0.5.ToString(CultureInfo.CurrentCulture)); // the culture really is in effect
            test();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }
}
