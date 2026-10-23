using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Protocol;

/// <summary>Exact-size boundaries of the input wire format and the argument guards of its writers.</summary>
public class WireBoundaryTests
{
    [Fact]
    public void Datagram_of_exactly_max_size_splits()
    {
        byte[] datagram = new byte[InputDatagram.MaxSize];
        datagram[0] = InputDatagramHeader.CurrentVersion;
        Assert.True(InputDatagram.TrySplit(datagram, out _, out _, out ReadOnlySpan<byte> ciphertext, out ReadOnlySpan<byte> tag));
        Assert.Equal(InputDatagram.MaxPayloadSize, ciphertext.Length);
        Assert.Equal(InputDatagram.TagSize, tag.Length);
    }

    [Fact]
    public void Touch_sample_reads_from_exactly_its_size()
    {
        byte[] touch = Hex.Bytes("07 02 0102 0304");
        Assert.True(InputSampleCodec.TryReadTouch(touch, out TouchSample sample));
        Assert.Equal(new TouchSample(7, true, 0x0102, 0x0304), sample);
        Assert.False(InputSampleCodec.TryReadTouch(touch.AsSpan(0, InputSampleCodec.TouchSampleSize - 1), out _));
    }

    [Fact]
    public void Nonce_guard_names_the_header()
    {
        Assert.Equal(
            "header",
            Assert.Throws<ArgumentOutOfRangeException>(() => InputDatagram.Nonce(new byte[InputDatagramHeader.Size - 1])).ParamName);
    }

    [Theory]
    [InlineData(InputPayload.MinSize, true)]
    [InlineData(InputDatagram.MaxPayloadSize, true)]
    [InlineData(InputPayload.MinSize - 1, false)]
    [InlineData(InputDatagram.MaxPayloadSize + 1, false)]
    public void Slice_accepts_payload_lengths_only_inside_the_bounds(int payloadLength, bool valid)
    {
        byte[] datagram = new byte[InputDatagram.MaxSize + 1];
        if (valid)
        {
            InputDatagram.Slice(datagram, payloadLength, out _, out Span<byte> payload, out Span<byte> tag);
            Assert.Equal(payloadLength, payload.Length);
            Assert.Equal(InputDatagram.TagSize, tag.Length);
        }
        else
        {
            Assert.Equal(
                "payloadLength",
                Assert.Throws<ArgumentOutOfRangeException>(() => InputDatagram.Slice(datagram, payloadLength, out _, out _, out _)).ParamName);
        }
    }

    [Fact]
    public void Slice_needs_a_buffer_of_the_full_datagram_size()
    {
        int payloadLength = InputPayload.MinSize;
        byte[] exact = new byte[InputDatagram.GetSize(payloadLength)];
        InputDatagram.Slice(exact, payloadLength, out _, out _, out Span<byte> tag);
        Assert.Equal(InputDatagram.TagSize, tag.Length);
        Assert.Equal(
            "datagram",
            Assert.Throws<ArgumentOutOfRangeException>(() => InputDatagram.Slice(exact.AsSpan(1), payloadLength, out _, out _, out _)).ParamName);
    }

    [Fact]
    public void Payload_setters_reject_counts_over_the_caps()
    {
        var payload = new InputPayload();
        Assert.Equal(
            "touches",
            Assert.Throws<ArgumentOutOfRangeException>(() => payload.SetTouches(new TouchSample[InputPayload.MaxTouches + 1])).ParamName);
        Assert.Equal(
            "history",
            Assert.Throws<ArgumentOutOfRangeException>(() => payload.SetHistory(new PenSample[InputPayload.MaxHistory + 1])).ParamName);
        Assert.True(payload.Touches.IsEmpty);
        Assert.True(payload.History.IsEmpty);
    }

    [Fact]
    public void Version_write_needs_two_bytes()
    {
        byte[] buffer = [0xAA];
        Assert.Equal(
            "destination",
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolVersion(1, 2).Write(buffer)).ParamName);
        Assert.Equal(0xAA, buffer[0]);
    }

    [Fact]
    public void Settings_writers_need_the_whole_payload_size()
    {
        var update = new SettingsUpdateMessage([SettingEntry.Bool(1, true)]);
        byte[] small = new byte[update.PayloadSize - 1];
        Assert.Equal("destination", Assert.Throws<ArgumentOutOfRangeException>(() => update.WritePayload(small)).ParamName);

        var snapshot = new SettingsSnapshotMessage([SettingEntry.Bool(1, true)]);
        Assert.Equal("destination", Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.WritePayload(small)).ParamName);
        Assert.Equal(new byte[small.Length], small);
    }
}
