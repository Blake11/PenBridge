using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Protocol;

/// <summary>
/// Deterministic random input: every parser either returns false/NeedMoreData or throws <see cref="ProtocolException"/>.
/// Anything else (IndexOutOfRange, ArgumentOutOfRange, overflow…) fails the test.
/// </summary>
public class FuzzTests
{
    private const int Iterations = 20_000;

    [Fact]
    public void Message_parsers_never_throw_on_garbage()
    {
        var random = new Random(0x5EED);
        var buffer = new byte[400];
        for (int i = 0; i < Iterations; i++)
        {
            int length = random.Next(0, buffer.Length);
            random.NextBytes(buffer.AsSpan(0, length));
            if (length >= 2 && random.Next(4) == 0)
            {
                // Bias towards plausible small length prefixes so the deeper fields get exercised.
                buffer[0] = 0;
                buffer[1] = (byte)random.Next(0, 40);
            }

            ReadOnlySpan<byte> input = buffer.AsSpan(0, length);
            _ = HelloMessage.TryRead(input, out _);
            _ = HelloAckMessage.TryRead(input, out _);
            _ = ClientPairRound1Message.TryRead(input, out _);
            _ = ServerPairRound1Message.TryRead(input, out _);
            _ = ClientPairRound2Message.TryRead(input, out _);
            _ = ServerPairRound2Message.TryRead(input, out _);
            _ = ClientPairConfirmMessage.TryRead(input, out _);
            _ = ServerPairConfirmMessage.TryRead(input, out _);
            _ = PairResultMessage.TryRead(input, out _);
            _ = AuthMessage.TryRead(input, out _);
            _ = SessionStartMessage.TryRead(input, out _);
            _ = SurfaceInfoMessage.TryRead(input, out _);
            _ = UdpAckMessage.TryRead(input, out _);
            _ = InputFrameMessage.TryRead(input, out _);
            _ = PingMessage.TryRead(input, out _);
            _ = PongMessage.TryRead(input, out _);
            _ = BusyMessage.TryRead(input, out _);
            _ = ErrorMessage.TryRead(input, out _);
            _ = ByeMessage.TryRead(input, out _);
            _ = SettingsSnapshotMessage.TryRead(input, out _);
            _ = SettingsUpdateMessage.TryRead(input, out _);
            _ = DisplayListMessage.TryRead(input, out _);
            _ = ActiveAreaMessage.TryRead(input, out _);
            _ = InputDatagram.TrySplit(input, out _, out _, out _, out _);
        }
    }

    [Fact]
    public void Input_payload_parser_never_throws_on_structured_garbage()
    {
        var random = new Random(0xB0B);
        var buffer = new byte[InputPayload.MaxSize + 20];
        int accepted = 0;
        for (int i = 0; i < Iterations; i++)
        {
            int length = random.Next(0, buffer.Length);
            random.NextBytes(buffer.AsSpan(0, length));
            if (length >= 7)
            {
                buffer[4] = (byte)random.Next(0, 3);
                buffer[5] = (byte)random.Next(0, 12);
            }

            if (InputPayload.TryRead(buffer.AsSpan(0, length), out InputPayload payload))
            {
                accepted++;
                Assert.InRange(payload.Touches.Length, 0, InputPayload.MaxTouches);
                Assert.InRange(payload.History.Length, 0, InputPayload.MaxHistory);
            }
        }

        Assert.True(accepted > 0, "fuzzer never produced a valid payload; bias is wrong");
    }

    [Fact]
    public void Settings_parsers_never_throw_on_structured_garbage()
    {
        var random = new Random(0x5E77);
        var buffer = new byte[600];
        int accepted = 0;
        for (int i = 0; i < Iterations; i++)
        {
            // count, then entries with plausible types and lengths and random values.
            int count = random.Next(0, 8);
            buffer[0] = (byte)count;
            int at = 1;
            for (int e = 0; e < count && at + 5 < buffer.Length - 40; e++)
            {
                int type = random.Next(0, 7);
                int length = random.Next(4) == 0 ? random.Next(0, 30) : type switch { 1 => 1, 2 or 3 => 4, 5 => 16, _ => random.Next(0, 30) };
                buffer[at] = 0;
                buffer[at + 1] = (byte)random.Next(0, 30);
                buffer[at + 2] = (byte)type;
                buffer[at + 3] = 0;
                buffer[at + 4] = (byte)length;
                random.NextBytes(buffer.AsSpan(at + 5, length));
                at += 5 + length;
            }

            int cut = random.Next(3) == 0 ? random.Next(0, at + 1) : at;
            ReadOnlySpan<byte> input = buffer.AsSpan(0, cut);
            if (SettingsSnapshotMessage.TryRead(input, out SettingsSnapshotMessage snapshot))
            {
                accepted++;
                Assert.InRange(snapshot.Entries.Count, 0, count);
                foreach (SettingEntry entry in snapshot.Entries)
                {
                    Assert.InRange((int)entry.Type, 1, 5);
                    Assert.InRange(entry.Size, SettingEntry.HeaderSize, int.MaxValue);
                }
            }

            _ = SettingsUpdateMessage.TryRead(input, out _);
            _ = DisplayListMessage.TryRead(input, out _);
        }

        Assert.True(accepted > 0, "fuzzer never produced a valid snapshot; bias is wrong");
    }

    [Fact]
    public void Frame_decoder_only_throws_protocol_exceptions()
    {
        var random = new Random(0xF4A3E);
        var buffer = new byte[300];
        for (int i = 0; i < Iterations; i++)
        {
            int length = random.Next(0, buffer.Length);
            random.NextBytes(buffer.AsSpan(0, length));
            if (length >= 5 && random.Next(2) == 0)
            {
                buffer[0] = 0;
                buffer[1] = 0;
                buffer[2] = 0;
                buffer[3] = (byte)random.Next(0, 255);
            }

            try
            {
                ReadOnlySpan<byte> remaining = buffer.AsSpan(0, length);
                while (ControlFrame.TryRead(remaining, out _, out _, out int consumed) != ControlFrameStatus.NeedMoreData)
                {
                    Assert.InRange(consumed, ControlFrame.HeaderSize, remaining.Length);
                    remaining = remaining[consumed..];
                }
            }
            catch (ProtocolException)
            {
            }
        }
    }

    [Fact]
    public async Task Stream_reader_only_throws_protocol_exceptions()
    {
        var random = new Random(0xAB1E);
        for (int i = 0; i < 2_000; i++)
        {
            var bytes = new byte[random.Next(0, 200)];
            random.NextBytes(bytes);
            if (bytes.Length >= 5)
            {
                bytes[0] = 0;
                bytes[1] = 0;
                bytes[2] = 0;
                bytes[4] = random.Next(2) == 0 ? (byte)0x30 : (byte)random.Next(0xF0, 0x100);
            }

            using var reader = new ControlFrameReader(new MemoryStream(bytes));
            try
            {
                while (await reader.ReadAsync(TestContext.Current.CancellationToken) is { } frame)
                {
                    _ = PingMessage.TryRead(frame.Payload.Span, out _);
                }
            }
            catch (ProtocolException)
            {
            }
        }
    }

    [Fact]
    public void Base64url_decoder_never_throws_on_43_char_garbage()
    {
        var random = new Random(0xBA5E);
        var chars = new char[Base64UrlBytes.EncodedLength32];
        for (int i = 0; i < Iterations; i++)
        {
            for (int c = 0; c < chars.Length; c++)
            {
                chars[c] = random.Next(8) == 0 ? (char)random.Next(0, 0x3000) : (char)random.Next(0x20, 0x7F);
            }

            if (Base64UrlBytes.TryDecode32(chars, out byte[] value))
            {
                Assert.Equal(new string(chars), Base64UrlBytes.Encode(value));
            }
        }
    }

    [Fact]
    public void Discovery_parsers_never_throw_on_garbage()
    {
        var random = new Random(0xD15C);
        const string alphabet = "penbridge://pair?h=&p=fp=t=v=1.0%%2F%C3%A9%FFAZaz09-_.[]:é😀";
        var chars = new char[200];
        for (int i = 0; i < Iterations; i++)
        {
            int length = random.Next(0, chars.Length);
            for (int c = 0; c < length; c++)
            {
                chars[c] = alphabet[random.Next(alphabet.Length)];
            }

            string text = new(chars, 0, length);
            _ = PairingQrPayload.TryParse(random.Next(2) == 0 ? PairingQrPayload.Prefix + text : text, out _);
            _ = BonjourTxtRecord.TryParse(text, text, text, out _);
            _ = ProtocolVersion.TryParse(text, out _);
        }
    }
}
