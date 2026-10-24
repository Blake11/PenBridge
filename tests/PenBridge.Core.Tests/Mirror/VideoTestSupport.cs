using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Tests.Security;

namespace PenBridge.Core.Tests.Mirror;

/// <summary>One opened fragment: its header and plaintext payload, as the phone's receive thread has them.</summary>
internal sealed record OpenedFragment(VideoDatagramHeader Header, byte[] Payload);

/// <summary>A Mac → phone video link: fragments with K_vs, opens them on the other side.</summary>
internal sealed class VideoLink : IDisposable
{
    public const uint Session = 0x0BADF00D;

    private static readonly byte[] Key = KeyScheduleTests.Fill(32, 77);

    public VideoCipher Tx { get; } = new(Key, Session);

    public VideoCipher Rx { get; } = new(Key, Session);

    /// <summary>Deterministic access-unit bytes for frame <paramref name="frame"/>.</summary>
    public static byte[] AccessUnit(int length, uint frame)
    {
        var bytes = new byte[length];
        new Random((int)frame * 31 + length).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Fragments and seals, then opens every datagram (each once) and returns the plaintext fragments.</summary>
    public List<OpenedFragment> Send(byte[] accessUnit, VideoFrameInfo frame)
    {
        int count = VideoFragmenter.GetFragmentCount(accessUnit.Length);
        var opened = new List<OpenedFragment>(count);
        byte[] datagram = new byte[VideoDatagram.MaxSize];
        for (int i = 0; i < count; i++)
        {
            int length = VideoFragmenter.WriteFragment(Tx, accessUnit, frame, i, datagram);
            Assert.True(Rx.TryOpen(datagram.AsSpan(0, length), out VideoDatagramHeader header, out int payloadLength));
            opened.Add(new OpenedFragment(header, datagram.AsSpan(VideoDatagramHeader.Size, payloadLength).ToArray()));
        }

        return opened;
    }

    public void Dispose()
    {
        Tx.Dispose();
        Rx.Dispose();
    }
}
