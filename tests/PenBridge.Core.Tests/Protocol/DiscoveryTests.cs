using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Protocol;

public class DiscoveryTests
{
    private static readonly byte[] Fingerprint = Hex.Range(0x00, 32);
    private static readonly byte[] Token = Hex.Range(0xE0, 32);
    private const string FingerprintText = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    [Fact]
    public void Txt_record_entries_golden()
    {
        var record = new BonjourTxtRecord(ProtocolInfo.Version, Fingerprint, "Paul’s Mac");
        Assert.Equal(
            [new("v", "1.2"), new("fp", FingerprintText), new("name", "Paul’s Mac")],
            record.ToEntries());
    }

    [Fact]
    public void Txt_record_round_trips_with_case_insensitive_keys_and_extra_entries()
    {
        KeyValuePair<string, string>[] entries = [new("V", "1.4"), new("FP", FingerprintText), new("Name", "Mac"), new("future", "x")];
        Assert.True(BonjourTxtRecord.TryParse(entries, out BonjourTxtRecord? record));
        Assert.Equal(new ProtocolVersion(1, 4), record!.Version);
        Assert.Equal(Fingerprint, record.Fingerprint.ToArray());
        Assert.Equal("Mac", record.Name);
    }

    [Theory]
    [InlineData("1.0", FingerprintText + "A", "Mac")]
    [InlineData("1.0", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", "Mac")]
    [InlineData("1.0", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh+", "Mac")]
    [InlineData("1.0", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9", "Mac")]
    [InlineData("1.0", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh 8", "Mac")]
    [InlineData("1", FingerprintText, "Mac")]
    [InlineData(null, FingerprintText, "Mac")]
    [InlineData("1.0", null, "Mac")]
    [InlineData("1.0", FingerprintText, null)]
    public void Txt_record_rejects_invalid_fields(string? v, string? fp, string? name) =>
        Assert.False(BonjourTxtRecord.TryParse(v, fp, name, out _));

    [Fact]
    public void Txt_record_rejects_duplicates_and_long_names()
    {
        KeyValuePair<string, string>[] duplicate = [new("v", "1.0"), new("fp", FingerprintText), new("name", "a"), new("NAME", "b")];
        Assert.False(BonjourTxtRecord.TryParse(duplicate, out _));
        Assert.False(BonjourTxtRecord.TryParse("1.0", FingerprintText, new string('x', 251), out _));
        Assert.Throws<ArgumentException>(() => new BonjourTxtRecord(ProtocolInfo.Version, Fingerprint, new string('x', 251)));
        Assert.Throws<ArgumentException>(() => new BonjourTxtRecord(ProtocolInfo.Version, new byte[31], "x"));
    }

    [Fact]
    public void Qr_payload_golden_and_round_trip()
    {
        var qr = new PairingQrPayload("192.168.1.20", 47820, Fingerprint, Token, ProtocolInfo.Version);
        string text = qr.Format();
        Assert.Equal(
            "penbridge://pair?h=192.168.1.20&p=47820&fp=" + FingerprintText + "&t=4OHi4-Tl5ufo6err7O3u7_Dx8vP09fb3-Pn6-_z9_v8&v=1.2",
            text);

        Assert.True(PairingQrPayload.TryParse(text, out PairingQrPayload? back));
        Assert.Equal("192.168.1.20", back!.Host);
        Assert.Equal(47820, back.Port);
        Assert.Equal(Fingerprint, back.Fingerprint.ToArray());
        Assert.Equal(Token, back.Token.ToArray());
        Assert.Equal(ProtocolInfo.Version, back.Version);
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("pauls-mac.local")]
    public void Qr_payload_escapes_hosts(string host)
    {
        var qr = new PairingQrPayload(host, 47821, Fingerprint, Token, ProtocolInfo.Version);
        Assert.True(PairingQrPayload.TryParse(qr.Format(), out PairingQrPayload? back));
        Assert.Equal(host, back!.Host);
        Assert.Equal(47821, back.Port);
    }

    [Fact]
    public void Qr_payload_accepts_any_key_order_and_ignores_unknown_keys()
    {
        string text = "penbridge://pair?v=1.2&x=9&t=4OHi4-Tl5ufo6err7O3u7_Dx8vP09fb3-Pn6-_z9_v8&fp=" + FingerprintText + "&p=1&h=10.0.0.1";
        Assert.True(PairingQrPayload.TryParse(text, out PairingQrPayload? qr));
        Assert.Equal(new ProtocolVersion(1, 2), qr!.Version);
    }

    [Fact]
    public void Qr_payload_to_string_redacts_the_token()
    {
        var qr = new PairingQrPayload("10.0.0.1", 47820, Fingerprint, Token, ProtocolInfo.Version);
        Assert.DoesNotContain("4OHi", qr.ToString(), StringComparison.Ordinal);
        Assert.Contains("redacted", qr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://pair?h=a&p=1&fp=x&t=y&v=1.0")]
    [InlineData("penbridge://pair?")]
    [InlineData("penbridge://pair?h=10.0.0.1&p=47820&fp=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h=10.0.0.1&p=0&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h=10.0.0.1&p=65536&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h=10.0.0.1&p=+1&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h=10.0.0.1&p=1&fp=AAAA&t=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h=10.0.0.1&p=1&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=x")]
    [InlineData("penbridge://pair?h=10.0.0.1&h=10.0.0.2&p=1&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h=bad host&p=1&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h=%zz&p=1&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h=%C3&p=1&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h=10.0.0.1&p=1&&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=1.0")]
    [InlineData("penbridge://pair?h&p=1&fp=" + FingerprintText + "&t=" + FingerprintText + "&v=1.0")]
    public void Qr_payload_rejects_invalid_text(string? text) => Assert.False(PairingQrPayload.TryParse(text, out _));

    [Fact]
    public void Qr_payload_rejects_oversized_text() =>
        Assert.False(PairingQrPayload.TryParse(PairingQrPayload.Prefix + new string('a', PairingQrPayload.MaxLength), out _));
}
