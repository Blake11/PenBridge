using System.Security.Cryptography;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Tests.Input;

namespace PenBridge.Core.Tests.Security;

[Collection(AllocationGroup.Name)]
public class VideoCipherTests
{
    private const uint Session = 0xA1B2C3D4;
    private readonly byte[] _key = KeyScheduleTests.Fill(32, 5);

    [Fact]
    public void Seal_then_open_round_trips_and_returns_the_header()
    {
        using var tx = new VideoCipher(_key, Session);
        using var rx = new VideoCipher(_key, Session);
        byte[] payload = KeyScheduleTests.Fill(100, 11);
        byte[] d = Build(tx, 1, payload, out int length, epoch: 3, frame: 9, index: 1, count: 2, keyframe: true);

        Assert.Equal(24 + 100 + 16, length);
        Assert.NotEqual(payload, d[24..124]);
        Assert.True(rx.TryOpen(d.AsSpan(0, length), out VideoDatagramHeader header, out int payloadLength));
        Assert.Equal(VideoDatagramHeader.Fragment(3, true, Session, 1, 9, 1, 2), header);
        Assert.Equal(100, payloadLength);
        Assert.Equal(payload, d.AsSpan(24, 100).ToArray());
        Assert.Equal(1UL, rx.HighestSequence);
        Assert.Equal(1UL, tx.LastSealedSequence);
    }

    [Fact]
    public void Nonce_is_session_id_then_sequence_and_the_header_is_the_aad()
    {
        using var tx = new VideoCipher(_key, Session);
        byte[] d = new byte[24 + 3 + 16];
        VideoDatagramHeader.Fragment(1, false, Session, 7, 2, 0, 1).Write(d);
        d[24] = 1; d[25] = 2; d[26] = 3;
        tx.Seal(d, 3);

        using var aes = new AesGcm(_key, 16);
        byte[] nonce = [0xA1, 0xB2, 0xC3, 0xD4, 0, 0, 0, 0, 0, 0, 0, 7];
        byte[] plain = new byte[3];
        aes.Decrypt(nonce, d.AsSpan(24, 3), d.AsSpan(27, 16), plain, d.AsSpan(0, 24));
        Assert.Equal([1, 2, 3], plain);
    }

    [Theory]
    [InlineData(0)]   // version
    [InlineData(2)]   // epoch
    [InlineData(3)]   // flags
    [InlineData(19)]  // frame number
    [InlineData(21)]  // fragment index
    [InlineData(30)]  // ciphertext
    [InlineData(-1)]  // tag
    public void Any_flipped_bit_fails(int offset)
    {
        using var tx = new VideoCipher(_key, Session);
        using var rx = new VideoCipher(_key, Session);
        byte[] d = Build(tx, 1, KeyScheduleTests.Fill(20, 1), out int length, count: 4);
        int at = offset < 0 ? length - 1 : offset;
        d[at] ^= 0x01;
        Assert.False(rx.TryOpen(d.AsSpan(0, length), out VideoDatagramHeader header, out int payloadLength));
        Assert.Equal(default, header);
        Assert.Equal(0, payloadLength);
        Assert.Equal(0UL, rx.HighestSequence);
    }

    [Fact]
    public void The_wrong_key_or_session_fails()
    {
        using var tx = new VideoCipher(_key, Session);
        using var otherKey = new VideoCipher(KeyScheduleTests.Fill(32, 6), Session);
        using var otherSession = new VideoCipher(_key, Session + 1);
        byte[] d = Build(tx, 1, [1, 2, 3], out int length);
        Assert.False(otherKey.TryOpen(d.AsSpan(0, length).ToArray(), out _, out _));
        Assert.False(otherSession.TryOpen(d.AsSpan(0, length).ToArray(), out _, out _));
    }

    [Fact]
    public void Seal_enforces_one_increasing_sequence_across_epochs()
    {
        using var tx = new VideoCipher(_key, Session);
        byte[] d = new byte[VideoDatagram.MaxSize];
        Assert.Throws<InvalidOperationException>(() => Seal(tx, d, 0, epoch: 1));
        Seal(tx, d, 5, epoch: 1);
        Seal(tx, d, 6, epoch: 2); // a new epoch keeps the sequence
        Assert.Throws<InvalidOperationException>(() => Seal(tx, d, 6, epoch: 3));
        Assert.Throws<InvalidOperationException>(() => Seal(tx, d, 2, epoch: 3));
        Assert.Equal(6UL, tx.LastSealedSequence);
        Seal(tx, d, 7, epoch: 3);
        Assert.Equal(7UL, tx.LastSealedSequence);

        static void Seal(VideoCipher c, byte[] d, ulong sequence, byte epoch)
        {
            VideoDatagramHeader.Probe(epoch, Session, sequence).Write(d);
            c.Seal(d, 0);
        }
    }

    [Fact]
    public void Seal_rejects_bad_buffers_and_headers()
    {
        using var tx = new VideoCipher(_key, Session);
        byte[] d = new byte[VideoDatagram.MaxSize + 10];
        VideoDatagramHeader.Probe(1, Session, 1).Write(d);
        Assert.Equal("datagram", Assert.Throws<ArgumentException>(() => tx.Seal(d, VideoDatagram.MaxPayloadSize + 1)).ParamName);
        Assert.Equal("datagram", Assert.Throws<ArgumentException>(() => tx.Seal(d.AsSpan(0, 24 + 10 + 15), 10)).ParamName);
        Assert.Equal("datagram", Assert.Throws<ArgumentException>(() => tx.Seal(d, -1)).ParamName);

        VideoDatagramHeader.Probe(1, Session + 1, 1).Write(d);
        Assert.Equal("datagram", Assert.Throws<ArgumentException>(() => tx.Seal(d, 0)).ParamName);

        VideoDatagramHeader.Probe(1, Session, 1).Write(d);
        d[0] = 2; // unknown version
        Assert.Equal("datagram", Assert.Throws<ArgumentException>(() => tx.Seal(d, 0)).ParamName);
        Assert.Equal(0UL, tx.LastSealedSequence);
    }

    [Fact]
    public void Open_accepts_reordering_once_inside_the_window()
    {
        using var tx = new VideoCipher(_key, Session);
        using var rx = new VideoCipher(_key, Session);
        var sealedDatagrams = new byte[2000][];
        for (int i = 1; i < sealedDatagrams.Length; i++)
        {
            sealedDatagrams[i] = Build(tx, (ulong)i, [(byte)i], out int length)[..length];
        }

        Assert.True(Open(rx, sealedDatagrams[3]));
        Assert.True(Open(rx, sealedDatagrams[1]));  // reordered, inside the window
        Assert.False(Open(rx, sealedDatagrams[1])); // replay
        Assert.False(Open(rx, sealedDatagrams[3])); // replay of the highest
        Assert.True(Open(rx, sealedDatagrams[2]));
        Assert.Equal(3UL, rx.HighestSequence);

        Assert.True(Open(rx, sealedDatagrams[1027])); // window now covers 4..1027
        Assert.True(Open(rx, sealedDatagrams[4]));    // exactly 1023 below: still inside
        Assert.False(Open(rx, sealedDatagrams[4]));
        Assert.True(Open(rx, sealedDatagrams[1028]));
        Assert.False(Open(rx, sealedDatagrams[4]));   // 1024 below: outside
        Assert.True(Open(rx, sealedDatagrams[5]));    // 1023 below, never seen

        Assert.True(Open(rx, sealedDatagrams[1999])); // a long jump: the window now covers 976..1999
        Assert.False(Open(rx, sealedDatagrams[975]));
        Assert.True(Open(rx, sealedDatagrams[976]));
        Assert.True(Open(rx, sealedDatagrams[1500]));
        Assert.False(Open(rx, sealedDatagrams[1500]));

        static bool Open(VideoCipher c, byte[] datagram) => c.TryOpen(datagram.ToArray(), out _, out _);
    }

    [Fact]
    public void A_forged_datagram_does_not_move_the_replay_window()
    {
        using var tx = new VideoCipher(_key, Session);
        using var rx = new VideoCipher(_key, Session);
        byte[] first = Build(tx, 1, [1], out int l1)[..l1];
        byte[] forged = Build(tx, 5000, [2], out int l2)[..l2];
        forged[^1] ^= 0xFF;
        Assert.False(rx.TryOpen(forged, out _, out _));
        Assert.Equal(0UL, rx.HighestSequence);
        Assert.True(rx.TryOpen(first, out _, out _));
    }

    [Fact]
    public void Open_drops_malformed_input_without_throwing()
    {
        using var tx = new VideoCipher(_key, Session);
        using var rx = new VideoCipher(_key, Session);
        Assert.False(rx.TryOpen(new byte[VideoDatagram.MinSize - 1], out _, out _));
        Assert.False(rx.TryOpen(new byte[VideoDatagram.MaxSize + 1], out _, out _));
        Assert.False(rx.TryOpen(new byte[VideoDatagram.MaxSize], out _, out _));

        byte[] zeroSequence = new byte[VideoDatagram.MinSize];
        VideoDatagramHeader.Probe(1, Session, 0).Write(zeroSequence);
        Assert.False(rx.TryOpen(zeroSequence, out _, out _));

        var random = new Random(42);
        byte[] garbage = new byte[VideoDatagram.MaxSize];
        for (int i = 0; i < 2000; i++)
        {
            int length = random.Next(0, garbage.Length + 1);
            random.NextBytes(garbage);
            if (i % 2 == 0 && length >= 24)
            {
                VideoDatagramHeader.Fragment(1, false, Session, (ulong)i + 1, 1, 0, 1).Write(garbage);
            }

            Assert.False(rx.TryOpen(garbage.AsSpan(0, length), out _, out _));
        }

        Assert.Equal(0UL, rx.HighestSequence);
    }

    [Fact]
    public void Disposed_cipher_throws()
    {
        var c = new VideoCipher(_key, Session);
        c.Dispose();
        c.Dispose();
        Assert.Throws<ObjectDisposedException>(() => c.Seal(new byte[100], 0));
        Assert.Throws<ObjectDisposedException>(() => c.TryOpen(new byte[100], out _, out _));
        Assert.Equal("videoKey", Assert.Throws<ArgumentException>(() => new VideoCipher(new byte[16], Session)).ParamName);
    }

    [Fact]
    public void Seal_and_open_do_not_allocate()
    {
        using var tx = new VideoCipher(_key, Session);
        using var rx = new VideoCipher(_key, Session);
        byte[] d = new byte[VideoDatagram.MaxSize];
        ulong sequence = 0;
        long opened = 0;
        Alloc.AssertZero(2_000, i =>
        {
            int payload = i % 3 == 0 ? VideoDatagram.MaxPayloadSize : 1 + (i % 500);
            VideoDatagramHeader.Fragment((byte)(i / 100), i % 7 == 0, Session, ++sequence, (uint)i + 1, 0, 1).Write(d);
            int length = tx.Seal(d, payload);
            if (rx.TryOpen(d.AsSpan(0, length), out _, out _))
            {
                opened++;
            }
        }, () => opened);
    }

    private static byte[] Build(VideoCipher tx, ulong sequence, byte[] payload, out int length, byte epoch = 1, uint frame = 1, ushort index = 0, ushort count = 1, bool keyframe = false)
    {
        byte[] d = new byte[VideoDatagram.MaxSize];
        VideoDatagramHeader.Fragment(epoch, keyframe, Session, sequence, frame, index, count).Write(d);
        payload.CopyTo(d, 24);
        length = tx.Seal(d, payload.Length);
        return d;
    }
}
