using System.Text;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Protocol;

/// <summary>
/// Inputs found by the coverage-guided campaign in <c>tools/PenBridge.Fuzz</c> (#262), replayed under the parser
/// contract: only <see cref="ProtocolException"/> from the frame layer, and Try* parsers never throw. A finding that
/// is a bug goes here as a test with <c>Skip = "#issue"</c> until its fix (README "After a finding").
/// </summary>
public class FuzzRegressionTests
{
    public static TheoryData<string, string> CorpusSamples => new()
    {
        // Frontier inputs from the 2026-10-22 campaign (the last corpus additions of each target).
        { "control", "000000011200000001120000000124000000012412364000011f040000012412364064641b000001f43f2380000000277fff343536373039" },
        { "control", "000000017f000000017f000000017f1300130000005b0200030000800001f4027f0103010001010101010101" },
        { "control", "00000009230000000010fa000000000009230000000010fa03d0000000092300000000000000000021000000808080801000808000807fff027f7f307f7f7f7f8080808001007e0101" },
        { "control", "00000001020000000102000000010200000001020001031010ffff000302000090010002dfd5fce748554173cf2d2ee022d0038405dcf63c2328010301009340" },
        { "payload", "0000002100010002006400c80102012c0100000002006400c80102012c01900700ffff00" },
        { "payload", "0000000300030102006400c80102012101900700ffff000000" },
        { "datagram", "000100f300000100010143b8d01700000100000200000017d20503e8cb67f0b8da0a009120c55e9910442328000200ffff00" },
        { "datagram", "0100000101010101833f21772386ffdc04010101010101000a0f0c0d0000004100000001b5ec119c757c83fa0000fa86ffdc04" },
        { "qr", "3532312e21c47c0000c325fc67d369723f6839506a380a5061756c0000000a204d6156636d696e69" },
        { "qr", "3532312e61c47ca432" },
    };

    [Theory]
    [MemberData(nameof(CorpusSamples))]
    public void Campaign_inputs_keep_the_parser_contract(string target, string hex)
    {
        byte[] input = Convert.FromHexString(hex);
        switch (target)
        {
            case "control":
                DecodeFrames(input);
                break;
            case "payload":
                _ = InputPayload.TryRead(input, out _);
                break;
            case "datagram":
                if (InputDatagram.TrySplit(input, out _, out _, out ReadOnlySpan<byte> ciphertext, out _))
                {
                    _ = InputPayload.TryRead(ciphertext, out _);
                }

                break;
            default:
                string text = Encoding.UTF8.GetString(input);
                string[] parts = text.Split('\n', 3);
                _ = PairingQrPayload.TryParse(text, out _);
                _ = BonjourTxtRecord.TryParse(parts[0], parts.Length > 1 ? parts[1] : null, parts.Length > 2 ? parts[2] : null, out _);
                _ = ProtocolVersion.TryParse(text, out _);
                break;
        }
    }

    [Fact]
    public void Settings_update_whose_entries_are_all_undecodable_parses_to_no_entries()
    {
        // Found by the campaign: wire count 1, one entry of unknown type 0 with no value. The reader skips it, so the
        // message has zero entries (documented on Entries). Re-encoding writes count 0, which the reader rejects; the
        // Mac never re-sends a parsed update, so only the apply path matters.
        Assert.True(SettingsUpdateMessage.TryRead(Hex.Bytes("01 0000 00 0000"), out SettingsUpdateMessage update));
        Assert.Empty(update.Entries);

        var values = new MacSettingValues();
        Assert.Empty(values.ApplyUpdate(update.Entries, []));
        values.ToInputSettings().Validate();
    }

    private static void DecodeFrames(ReadOnlySpan<byte> rest)
    {
        try
        {
            while (ControlFrame.TryRead(rest, out ControlMessageType _, out ReadOnlySpan<byte> payload, out int consumed) != ControlFrameStatus.NeedMoreData)
            {
                Assert.InRange(consumed, ControlFrame.HeaderSize, rest.Length);
                ParseAll(payload);
                rest = rest[consumed..];
            }
        }
        catch (ProtocolException)
        {
        }
    }

    private static void ParseAll(ReadOnlySpan<byte> p)
    {
        _ = HelloMessage.TryRead(p, out _);
        _ = HelloAckMessage.TryRead(p, out _);
        _ = ClientPairRound1Message.TryRead(p, out _);
        _ = ServerPairRound1Message.TryRead(p, out _);
        _ = ClientPairRound2Message.TryRead(p, out _);
        _ = ServerPairRound2Message.TryRead(p, out _);
        _ = ClientPairConfirmMessage.TryRead(p, out _);
        _ = ServerPairConfirmMessage.TryRead(p, out _);
        _ = PairResultMessage.TryRead(p, out _);
        _ = AuthMessage.TryRead(p, out _);
        _ = SessionStartMessage.TryRead(p, out _);
        _ = SurfaceInfoMessage.TryRead(p, out _);
        _ = UdpAckMessage.TryRead(p, out _);
        _ = InputFrameMessage.TryRead(p, out _);
        _ = PingMessage.TryRead(p, out _);
        _ = PongMessage.TryRead(p, out _);
        _ = BusyMessage.TryRead(p, out _);
        _ = ErrorMessage.TryRead(p, out _);
        _ = ByeMessage.TryRead(p, out _);
        _ = SettingsSnapshotMessage.TryRead(p, out _);
        _ = SettingsUpdateMessage.TryRead(p, out _);
        _ = DisplayListMessage.TryRead(p, out _);
        _ = ActiveAreaMessage.TryRead(p, out _);
    }
}
