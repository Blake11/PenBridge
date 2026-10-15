using System.Buffers.Binary;
using PenBridge.Core.Security;
using PenBridge.Core.Tests.Input;

namespace PenBridge.Core.Tests.Security;

[Collection(AllocationGroup.Name)]
public class InputCipherTests
{
    private const uint Session = 0xA1B2C3D4;
    private readonly byte[] _key = KeyScheduleTests.Fill(32, 5);

    [Fact]
    public void Seal_then_open_round_trips_and_uses_header_as_aad()
    {
        using var tx = new InputCipher(_key, Session);
        using var rx = new InputCipher(_key, Session);
        byte[] payload = KeyScheduleTests.Fill(40, 11);
        byte[] datagram = Build(Session, 1, payload, out int length);

        Assert.Equal(16 + 40 + 16, length);
        Assert.False(datagram.AsSpan(16, 40).SequenceEqual(payload)); // encrypted
        Assert.True(rx.TryOpen(datagram.AsSpan(0, length), out int payloadLength));
        Assert.Equal(40, payloadLength);
        Assert.Equal(payload, datagram.AsSpan(16, 40).ToArray());
        Assert.Equal(1UL, rx.HighestSequence);

        byte[] Build(uint session, ulong seq, byte[] p, out int len)
        {
            byte[] d = new byte[512];
            WriteHeader(d, session, seq);
            p.CopyTo(d, 16);
            len = tx.Seal(d, p.Length);
            return d;
        }
    }

    [Fact]
    public void Nonce_is_session_id_then_sequence()
    {
        // Cross-check against a raw AesGcm with nonce = header[4..16].
        using var tx = new InputCipher(_key, Session);
        byte[] d = new byte[16 + 3 + 16];
        WriteHeader(d, Session, 7);
        d[16] = 1; d[17] = 2; d[18] = 3;
        tx.Seal(d, 3);

        using var aes = new System.Security.Cryptography.AesGcm(_key, 16);
        byte[] nonce = [0xA1, 0xB2, 0xC3, 0xD4, 0, 0, 0, 0, 0, 0, 0, 7];
        byte[] plain = new byte[3];
        aes.Decrypt(nonce, d.AsSpan(16, 3), d.AsSpan(19, 16), plain, d.AsSpan(0, 16));
        Assert.Equal([1, 2, 3], plain);
    }

    [Theory]
    [InlineData(0)]   // version byte (header is AAD)
    [InlineData(1)]   // flags
    [InlineData(16)]  // ciphertext
    [InlineData(30)]  // tag
    public void Tampered_datagram_is_rejected_and_does_not_advance_window(int index)
    {
        using var tx = new InputCipher(_key, Session);
        using var rx = new InputCipher(_key, Session);
        byte[] d = Sealed(tx, 5, 10, out int len);

        d[index] ^= 0x01;
        Assert.False(rx.TryOpen(d.AsSpan(0, len), out _));
        Assert.Equal(0UL, rx.HighestSequence);

        byte[] good = Sealed(tx, 6, 10, out len);
        Assert.True(rx.TryOpen(good.AsSpan(0, len), out _));
    }

    [Fact]
    public void Replay_and_old_sequences_are_dropped_latest_wins()
    {
        using var tx = new InputCipher(_key, Session);
        using var rx = new InputCipher(_key, Session);
        byte[] d3 = Sealed(tx, 3, 8, out int len3);
        byte[] d3Copy = (byte[])d3.Clone();
        byte[] d5 = Sealed(tx, 5, 8, out int len5);

        Assert.True(rx.TryOpen(d3.AsSpan(0, len3), out _));
        Assert.False(rx.TryOpen(d3Copy.AsSpan(0, len3), out _)); // replay
        Assert.True(rx.TryOpen(d5.AsSpan(0, len5), out _));
        Assert.Equal(5UL, rx.HighestSequence);

        using var tx2 = new InputCipher(_key, Session);
        byte[] d4 = Sealed(tx2, 4, 8, out int len4); // reordered, older than 5
        Assert.False(rx.TryOpen(d4.AsSpan(0, len4), out _));
    }

    [Fact]
    public void Rejects_foreign_session_wrong_key_and_bad_sizes()
    {
        using var tx = new InputCipher(_key, Session);
        using var rx = new InputCipher(_key, Session + 1);
        byte[] d = Sealed(tx, 1, 8, out int len);
        Assert.False(rx.TryOpen(d.AsSpan(0, len), out _));

        using var wrongKey = new InputCipher(KeyScheduleTests.Fill(32, 6), Session);
        Assert.False(wrongKey.TryOpen(d.AsSpan(0, len), out _));

        using var ok = new InputCipher(_key, Session);
        Assert.False(ok.TryOpen(d.AsSpan(0, 31), out _));
        Assert.False(ok.TryOpen(new byte[513], out _));
        Assert.False(ok.TryOpen([], out _));
    }

    [Fact]
    public void Seal_enforces_increasing_sequence_and_matching_session()
    {
        using var tx = new InputCipher(_key, Session);
        byte[] d = new byte[64];
        WriteHeader(d, Session, 0);
        Assert.Throws<InvalidOperationException>(() => tx.Seal(d, 8)); // sequence starts at 1

        WriteHeader(d, Session, 2);
        tx.Seal(d, 8);
        WriteHeader(d, Session, 2);
        Assert.Throws<InvalidOperationException>(() => tx.Seal(d, 8)); // nonce reuse
        WriteHeader(d, Session, 1);
        Assert.Throws<InvalidOperationException>(() => tx.Seal(d, 8));

        WriteHeader(d, Session + 1, 3);
        Assert.Throws<ArgumentException>(() => tx.Seal(d, 8));
        WriteHeader(d, Session, 3);
        Assert.Throws<ArgumentException>(() => tx.Seal(d, 64));
        Assert.Throws<ArgumentException>(() => tx.Seal(new byte[600], InputCipher.MaxPayloadLength + 1));
    }

    [Fact]
    public void Seal_and_open_do_not_allocate()
    {
        using var tx = new InputCipher(_key, Session);
        using var rx = new InputCipher(_key, Session);
        byte[] buffer = new byte[InputCipher.MaxDatagramLength];
        ulong seq = 0;

        void RoundTrip()
        {
            seq++;
            WriteHeader(buffer, Session, seq);
            buffer.AsSpan(16, 64).Fill(0x42);
            int len = tx.Seal(buffer, 64);
            if (!rx.TryOpen(buffer.AsSpan(0, len), out _))
            {
                throw new InvalidOperationException("open failed");
            }
        }

        long opened = 0;
        Alloc.AssertZero(1_000, _ =>
        {
            RoundTrip();
            opened++;
        }, () => opened);
    }

    private static byte[] Sealed(InputCipher tx, ulong seq, int payloadLength, out int length)
    {
        byte[] d = new byte[16 + payloadLength + 16];
        WriteHeader(d, tx.SessionId, seq);
        d.AsSpan(16, payloadLength).Fill(0x5A);
        length = tx.Seal(d, payloadLength);
        return d;
    }

    private static void WriteHeader(Span<byte> d, uint session, ulong seq)
    {
        d[0] = 1;
        d[1] = 0;
        d[2] = 0;
        d[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], session);
        BinaryPrimitives.WriteUInt64BigEndian(d[8..], seq);
    }
}
