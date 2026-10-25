namespace PenBridge.Core.Mirror;

/// <summary>
/// One reassembled access unit (Annex-B), a handle to memory leased from the <see cref="FrameReassembler"/>'s pool. The
/// head queues <see cref="Data"/> to its decoder and then must <see cref="Dispose"/> the frame, which returns its memory;
/// after that the data is reused. Until disposed the frame holds pool memory, and with every frame held the reassembler
/// drops new frames. Dispose is thread-safe and idempotent: each lease has its own generation, so a repeated Dispose
/// (also through a copy of the handle, also after the memory went to a later frame) is ignored. <c>default</c> is an
/// empty frame.
/// </summary>
public readonly struct VideoFrame : IDisposable
{
    // The pool and the range are captured at lease time, so a span built from them is always in bounds; the generation
    // is checked before and after building it, so a handle used concurrently with its Dispose yields empty, not throws.
    private readonly FrameLease? _lease;
    private readonly byte[]? _buffer;
    private readonly int _offset;
    private readonly int _generation;
    private readonly int _length;

    internal VideoFrame(FrameLease lease, int generation, byte[] buffer, int offset, int length, byte epoch, uint frameNumber, bool keyframe, uint captureTimeUs, long completedAt)
    {
        _lease = lease;
        _buffer = buffer;
        _offset = offset;
        _generation = generation;
        _length = length;
        Epoch = epoch;
        FrameNumber = frameNumber;
        IsKeyframe = keyframe;
        CaptureTimeUs = captureTimeUs;
        CompletedTimestamp = completedAt;
    }

    /// <summary>The MirrorConfig epoch the frame belongs to.</summary>
    public byte Epoch { get; }

    public uint FrameNumber { get; }

    /// <summary>The frame is a keyframe (starts with its parameter sets).</summary>
    public bool IsKeyframe { get; }

    /// <summary>Capture time on the Mac's session clock, µs (wraps).</summary>
    public uint CaptureTimeUs { get; }

    /// <summary>When the last fragment arrived (<see cref="TimeProvider.GetTimestamp"/> of the reassembler's clock).</summary>
    public long CompletedTimestamp { get; }

    /// <summary>True until the frame is disposed (false for <c>default</c>).</summary>
    public bool IsValid => _lease is { } lease && lease.IsCurrent(_generation);

    /// <summary>Length of the access unit in bytes; 0 after <see cref="Dispose"/>.</summary>
    public int Length => IsValid ? _length : 0;

    /// <summary>The access unit; empty after <see cref="Dispose"/>.</summary>
    public ReadOnlyMemory<byte> Data
    {
        get
        {
            if (!IsValid)
            {
                return default;
            }

            var data = new ReadOnlyMemory<byte>(_buffer, _offset, _length);
            return IsValid ? data : default;
        }
    }

    /// <summary>The access unit as a span; empty after <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            if (!IsValid)
            {
                return default;
            }

            var span = new ReadOnlySpan<byte>(_buffer, _offset, _length);
            return IsValid ? span : default;
        }
    }

    /// <summary>Returns the frame's memory to the pool (once; later calls are ignored).</summary>
    public void Dispose() => _lease?.Owner.Release(_lease, _generation);
}

/// <summary>A reusable lease of one pool range; its generation changes on every return, invalidating old handles.</summary>
internal sealed class FrameLease(FrameReassembler owner)
{
    private int _generation;

    public FrameReassembler Owner { get; } = owner;

    public int Range { get; private set; } = -1;

    public bool Leased { get; private set; }

    public int Generation => Volatile.Read(ref _generation);

    public bool IsCurrent(int generation) => Volatile.Read(ref _generation) == generation;

    /// <summary>Leases the range; returns the generation the handle carries.</summary>
    public int Lease(int range)
    {
        Range = range;
        Leased = true;
        return Generation;
    }

    public void Return()
    {
        Volatile.Write(ref _generation, unchecked(_generation + 1));
        Range = -1;
        Leased = false;
    }
}
