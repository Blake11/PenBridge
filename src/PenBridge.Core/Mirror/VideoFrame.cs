namespace PenBridge.Core.Mirror;

/// <summary>
/// One reassembled access unit (Annex-B), leased from the <see cref="FrameReassembler"/>'s pool. The head queues
/// <see cref="Data"/> to its decoder and then must <see cref="Dispose"/> the frame, which returns its memory; after
/// that the data is reused. Until disposed the frame holds pool memory, and with every frame held the reassembler
/// drops new frames. Dispose is thread-safe. The object itself is reused for a later frame: dispose it exactly once and
/// drop the reference (a repeated Dispose before it is handed out again is ignored).
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private readonly FrameReassembler _owner;
    private byte[] _buffer = [];
    private int _offset;

    internal VideoFrame(FrameReassembler owner) => _owner = owner;

    /// <summary>The MirrorConfig epoch the frame belongs to.</summary>
    public byte Epoch { get; private set; }

    public uint FrameNumber { get; private set; }

    /// <summary>The frame is a keyframe (starts with its parameter sets).</summary>
    public bool IsKeyframe { get; private set; }

    /// <summary>Capture time on the Mac's session clock, µs (wraps).</summary>
    public uint CaptureTimeUs { get; private set; }

    /// <summary>When the last fragment arrived (<see cref="TimeProvider.GetTimestamp"/> of the reassembler's clock).</summary>
    public long CompletedTimestamp { get; private set; }

    /// <summary>Length of the access unit in bytes.</summary>
    public int Length { get; private set; }

    /// <summary>The access unit; empty after <see cref="Dispose"/>.</summary>
    public ReadOnlyMemory<byte> Data => new(_buffer, _offset, Length);

    /// <summary>The access unit as a span; empty after <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> Span => new(_buffer, _offset, Length);

    internal int Range { get; private set; } = -1;

    internal bool Leased { get; private set; }

    /// <summary>Returns the frame's memory to the pool.</summary>
    public void Dispose() => _owner.Release(this);

    internal void Lease(byte[] buffer, int offset, int length, int range, byte epoch, uint frameNumber, bool keyframe, uint captureTimeUs, long completedAt)
    {
        _buffer = buffer;
        _offset = offset;
        Length = length;
        Range = range;
        Epoch = epoch;
        FrameNumber = frameNumber;
        IsKeyframe = keyframe;
        CaptureTimeUs = captureTimeUs;
        CompletedTimestamp = completedAt;
        Leased = true;
    }

    internal void Return()
    {
        _buffer = [];
        _offset = 0;
        Length = 0;
        Range = -1;
        Leased = false;
    }
}
