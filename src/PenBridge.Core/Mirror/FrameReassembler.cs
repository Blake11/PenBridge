using System.Buffers.Binary;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Mirror;

/// <summary>What <see cref="FrameReassembler.Accept"/> did with one fragment.</summary>
public enum ReassemblyResult
{
    /// <summary>Stored; its frame is still incomplete.</summary>
    Buffered,

    /// <summary>The frame completed and is handed out (decode it, then dispose it).</summary>
    FrameReady,

    /// <summary>The frame completed but the decoder needs a keyframe first, so it was discarded.</summary>
    Gated,

    /// <summary>A fragment already stored.</summary>
    Duplicate,

    /// <summary>A fragment of a frame that already finished (completed, dropped or older than the last complete one).</summary>
    Stale,

    /// <summary>Another epoch's datagram.</summary>
    WrongEpoch,

    /// <summary>Not a fragment, inconsistent with its frame's other fragments, or a bad frame length.</summary>
    Invalid,

    /// <summary>No frame slot or pool memory for it (frames in flight or held by the decoder); the frame is lost.</summary>
    NoBuffer,

    /// <summary>Not started (<see cref="FrameReassembler.Start"/>).</summary>
    Inactive,
}

/// <summary>
/// Reassembles VideoFragment datagrams into access units on the phone (<c>docs/specs/screen-mirroring.md</c>
/// "Reassembly"): at most <see cref="MaxFramesInFlight"/> frames in flight, one <see cref="PoolBytes"/> pool for all of
/// them and the frames the decoder still holds, no allocation per datagram.
/// </summary>
/// <remarks>
/// <para><b>Dropping.</b> A frame is dropped when a newer frame completes before it, <see cref="FrameTimeout"/> after its
/// first fragment, when a fifth frame starts (the oldest goes), or when the pool has no room for it. A gap in frame
/// numbers between two completed frames is a lost frame too (the sender numbers every access unit, so a frame it
/// dropped unsent also shows as a gap).</para>
/// <para><b>Keyframe gating.</b> After <see cref="Start"/> and after any loss the decoder is fed nothing until a
/// keyframe completes (<see cref="ReassemblyResult.Gated"/>). After a loss, or when a non-keyframe completes while
/// waiting, a KeyframeRequest is due at once and then every <see cref="KeyframeRequestInterval"/> until a keyframe
/// arrives (<see cref="TryTakeKeyframeRequest"/>); after <see cref="Start"/> the first request waits one interval for
/// the epoch's first keyframe.</para>
/// <para><b>Counters</b> (since <see cref="Start"/>, wrap, for MirrorReport): frames handed out; frames dropped (including
/// gated and never-seen ones); fragments lost (the missing fragments of dropped frames, one per never-seen frame). Each
/// frame counts once, however many drop between two completed frames.</para>
/// <para><b>Driving it.</b> The receive thread opens each datagram (<see cref="Security.VideoCipher.TryOpen"/>) and
/// passes the header and plaintext to <see cref="Accept"/>; it waits on the socket at most <see cref="NextDeadline"/> and
/// calls <see cref="Poll"/> and <see cref="TryTakeKeyframeRequest"/> when it passes. Thread-safe (one internal lock);
/// <see cref="VideoFrame.Dispose"/> may run on the decoder's thread.</para>
/// </remarks>
public sealed class FrameReassembler
{
    /// <summary>Most frames being reassembled at once.</summary>
    public const int MaxFramesInFlight = 4;

    /// <summary>Most complete frames handed out and not yet disposed.</summary>
    public const int MaxFramesHeld = 4;

    /// <summary>
    /// Memory for every frame in flight and held: 2 MiB plus two fragments, so two frames of the largest size always fit
    /// (a frame in flight reserves up to one fragment beyond its length).
    /// </summary>
    public const int PoolBytes = (2 * VideoDatagram.MaxFrameLength) + (2 * VideoDatagram.MaxPayloadSize);

    /// <summary>A frame not complete this long after its first fragment is dropped.</summary>
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>KeyframeRequests repeat at this interval until a keyframe arrives.</summary>
    public static readonly TimeSpan KeyframeRequestInterval = TimeSpan.FromMilliseconds(250);

    private const int RangeCount = MaxFramesInFlight + MaxFramesHeld;
    // Dropped frame numbers are remembered exactly in a window above the last assembled frame; anything older than the
    // window counts as dropped (and its late fragments as stale).
    private const int DropWindow = 256;
    private const int DropWords = DropWindow / 64;

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly byte[] _pool = new byte[PoolBytes];
    private readonly Slot[] _slots = new Slot[MaxFramesInFlight];
    private readonly FrameLease[] _frames = new FrameLease[MaxFramesHeld];
    private readonly int[] _rangeOffset = new int[RangeCount];
    private readonly int[] _rangeLength = new int[RangeCount];
    private readonly bool[] _rangeUsed = new bool[RangeCount];
    private readonly ulong[] _dropBits = new ulong[DropWords];
    private readonly long _timeoutTicks;
    private readonly long _requestTicks;

    private bool _active;
    private byte _epoch;
    private bool _haveAssembled;
    private uint _lastAssembled;
    private bool _haveDelivered;
    private uint _lastDelivered;
    private uint _dropBase;
    private uint _dropsBelowBase;
    private bool _gated;
    private bool _requesting;
    private bool _inGrace;
    private long _requestDue;
    private uint _framesComplete;
    private uint _framesDropped;
    private uint _fragmentsLost;
    private ulong _rejected;

    public FrameReassembler(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _timeoutTicks = (long)(FrameTimeout.TotalSeconds * time.TimestampFrequency);
        _requestTicks = (long)(KeyframeRequestInterval.TotalSeconds * time.TimestampFrequency);
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new Slot();
        }

        for (int i = 0; i < _frames.Length; i++)
        {
            _frames[i] = new FrameLease(this);
        }
    }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    /// <summary>The current epoch.</summary>
    public byte Epoch
    {
        get
        {
            lock (_lock)
            {
                return _epoch;
            }
        }
    }

    /// <summary>True while the decoder waits for a keyframe.</summary>
    public bool KeyframeNeeded
    {
        get
        {
            lock (_lock)
            {
                return _gated;
            }
        }
    }

    /// <summary>Frames handed out since <see cref="Start"/> (wraps).</summary>
    public uint FramesComplete
    {
        get
        {
            lock (_lock)
            {
                return _framesComplete;
            }
        }
    }

    /// <summary>Frames dropped since <see cref="Start"/>, including gated and never-seen ones (wraps).</summary>
    public uint FramesDropped
    {
        get
        {
            lock (_lock)
            {
                return _framesDropped;
            }
        }
    }

    /// <summary>Fragments lost since <see cref="Start"/> (wraps).</summary>
    public uint FragmentsLost
    {
        get
        {
            lock (_lock)
            {
                return _fragmentsLost;
            }
        }
    }

    /// <summary>The last frame handed out in this epoch, 0 before the first.</summary>
    public uint LastCompleteFrame
    {
        get
        {
            lock (_lock)
            {
                return _haveDelivered ? _lastDelivered : 0;
            }
        }
    }

    /// <summary>Datagrams not stored (duplicate, stale, wrong epoch, invalid, no buffer) since this instance was created.</summary>
    public ulong DatagramsRejected
    {
        get
        {
            lock (_lock)
            {
                return _rejected;
            }
        }
    }

    /// <summary>
    /// Starts a new epoch (each MirrorConfig): drops every frame in flight unaccounted, zeroes the counters and waits
    /// for a keyframe. Frames still held by the decoder stay valid until disposed.
    /// </summary>
    public void Start(byte epoch)
    {
        lock (_lock)
        {
            ClearSlots();
            _active = true;
            _epoch = epoch;
            _haveAssembled = false;
            _haveDelivered = false;
            _lastAssembled = 0;
            _lastDelivered = 0;
            ClearDrops();
            _framesComplete = 0;
            _framesDropped = 0;
            _fragmentsLost = 0;
            _gated = true;
            _requesting = true;
            _inGrace = true;
            _requestDue = _time.GetTimestamp() + _requestTicks;
        }
    }

    /// <summary>Stops (MirrorStop, session end): drops every frame in flight; <see cref="Accept"/> then returns <see cref="ReassemblyResult.Inactive"/>.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            ClearSlots();
            _active = false;
            _requesting = false;
        }
    }

    /// <summary>
    /// Takes one authenticated datagram: its header and decrypted payload (<see cref="VideoDatagram"/> layout).
    /// <paramref name="frame"/> is set only for <see cref="ReassemblyResult.FrameReady"/> (else <c>default</c>). Never
    /// throws for bad input.
    /// </summary>
    public ReassemblyResult Accept(in VideoDatagramHeader header, ReadOnlySpan<byte> payload, out VideoFrame frame)
    {
        frame = default;
        lock (_lock)
        {
            if (!_active)
            {
                return ReassemblyResult.Inactive;
            }

            long now = _time.GetTimestamp();
            Expire(now);
            if (header.Kind != VideoDatagramKind.VideoFragment || !header.IsConsistent)
            {
                return Reject(ReassemblyResult.Invalid);
            }

            if (header.Epoch != _epoch)
            {
                return Reject(ReassemblyResult.WrongEpoch);
            }

            uint number = header.FrameNumber;
            if ((_haveAssembled && number <= _lastAssembled) || WasDropped(number))
            {
                return Reject(ReassemblyResult.Stale);
            }

            int index = header.FragmentIndex;
            ReadOnlySpan<byte> data = payload;
            uint captureTimeUs = 0;
            int frameLength = -1;
            if (index == 0)
            {
                if (payload.Length < VideoDatagram.FirstFragmentPrefixSize)
                {
                    return Reject(ReassemblyResult.Invalid);
                }

                captureTimeUs = BinaryPrimitives.ReadUInt32BigEndian(payload);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
                if (length is 0 or > VideoDatagram.MaxFrameLength || length > Capacity(header.FragmentCount))
                {
                    return Reject(ReassemblyResult.Invalid);
                }

                frameLength = (int)length;
                data = payload[VideoDatagram.FirstFragmentPrefixSize..];
            }

            if (data.Length > (index == 0 ? VideoDatagram.FirstFragmentDataSize : VideoDatagram.MaxPayloadSize) || (index != 0 && data.IsEmpty))
            {
                return Reject(ReassemblyResult.Invalid);
            }

            Slot? slot = Find(number);
            if (slot is null)
            {
                ReassemblyResult result = TryAllocate(number, header.FragmentCount, header.IsKeyframe, now, out slot);
                if (slot is null)
                {
                    return Reject(result);
                }
            }
            else if (slot.Count != header.FragmentCount || slot.Keyframe != header.IsKeyframe)
            {
                return Reject(ReassemblyResult.Invalid);
            }

            if (slot.Has(index))
            {
                return Reject(ReassemblyResult.Duplicate);
            }

            if (index == 0)
            {
                slot.FrameLength = frameLength;
                slot.CaptureTimeUs = captureTimeUs;
            }

            data.CopyTo(_pool.AsSpan(_rangeOffset[slot.Range] + VideoFragmenter.DataOffset(index)));
            slot.Mark(index, data.Length);
            return slot.Received < slot.Count ? ReassemblyResult.Buffered : Complete(slot, now, out frame);
        }
    }

    /// <summary>Drops frames past their timeout; call when <see cref="NextDeadline"/> passes without datagrams.</summary>
    public void Poll()
    {
        lock (_lock)
        {
            if (_active)
            {
                Expire(_time.GetTimestamp());
            }
        }
    }

    /// <summary>Time until the next frame timeout or KeyframeRequest; null when nothing is pending.</summary>
    public TimeSpan? NextDeadline
    {
        get
        {
            lock (_lock)
            {
                if (!_active)
                {
                    return null;
                }

                long now = _time.GetTimestamp();
                long due = long.MaxValue;
                foreach (Slot slot in _slots)
                {
                    if (slot.InUse)
                    {
                        due = Math.Min(due, slot.StartedAt + _timeoutTicks);
                    }
                }

                if (_requesting)
                {
                    due = Math.Min(due, _requestDue);
                }

                return due == long.MaxValue ? null : TimeSpan.FromSeconds(Math.Max(0, due - now) / (double)_time.TimestampFrequency);
            }
        }
    }

    /// <summary>
    /// True when a KeyframeRequest is due now; <paramref name="request"/> carries the epoch and the last frame handed
    /// out. The next one is due <see cref="KeyframeRequestInterval"/> later, until a keyframe arrives.
    /// </summary>
    public bool TryTakeKeyframeRequest(out KeyframeRequestMessage request)
    {
        request = default;
        lock (_lock)
        {
            long now = _time.GetTimestamp();
            if (!_active)
            {
                return false;
            }

            Expire(now);
            if (!_requesting || now < _requestDue)
            {
                return false;
            }

            _requestDue = now + _requestTicks;
            _inGrace = false;
            request = new KeyframeRequestMessage(_epoch, _haveDelivered ? _lastDelivered : 0);
            return true;
        }
    }

    /// <summary>
    /// The decoder lost frames on its own (its queue overflowed, it restarted): gate like a loss, so nothing but a
    /// keyframe is handed out next, with a KeyframeRequest due at once (unless one is already pending) and then every
    /// <see cref="KeyframeRequestInterval"/> until a keyframe arrives. Ignored when not active.
    /// </summary>
    public void RequestKeyframe()
    {
        lock (_lock)
        {
            if (!_active)
            {
                return;
            }

            _gated = true;
            StartRequesting(_time.GetTimestamp());
        }
    }

    /// <summary>The MirrorReport of this epoch's counters with the head's last render.</summary>
    public MirrorReportMessage CreateReport(uint lastRenderedFrame, ulong lastRenderedAtUs)
    {
        lock (_lock)
        {
            return new MirrorReportMessage(_epoch, _framesComplete, _framesDropped, _fragmentsLost, lastRenderedFrame, lastRenderedAtUs);
        }
    }

    internal void Release(FrameLease lease, int generation)
    {
        lock (_lock)
        {
            if (!lease.Leased || lease.Generation != generation)
            {
                return;
            }

            _rangeUsed[lease.Range] = false;
            lease.Return();
        }
    }

    private static int Capacity(int fragmentCount) => VideoFragmenter.DataOffset(fragmentCount - 1) + VideoDatagram.MaxPayloadSize;

    private ReassemblyResult Reject(ReassemblyResult result)
    {
        _rejected++;
        return result;
    }

    private ReassemblyResult Complete(Slot slot, long now, out VideoFrame frame)
    {
        frame = default;
        uint number = slot.Frame;
        int written = Compact(slot);
        if (written != slot.FrameLength)
        {
            Drop(slot, now);
            return ReassemblyResult.Invalid;
        }

        foreach (Slot other in _slots)
        {
            if (other.InUse && other != slot && other.Frame < number)
            {
                Drop(other, now);
            }
        }

        // Frames between the last assembled one and this one were dropped (already counted) or never seen (counted
        // now, one lost fragment each). The epoch's first frame is gated anyway; the drops before it are forgotten.
        bool loss = false;
        uint seen = TakeDropsBelow(number);
        if (_haveAssembled)
        {
            uint skipped = number - _lastAssembled - 1;
            if (skipped > 0)
            {
                loss = true;
                uint neverSeen = skipped > seen ? skipped - seen : 0;
                _framesDropped = unchecked(_framesDropped + neverSeen);
                _fragmentsLost = unchecked(_fragmentsLost + neverSeen);
            }
        }

        _haveAssembled = true;
        _lastAssembled = number;

        if ((loss || _gated) && !slot.Keyframe)
        {
            _gated = true;
            StartRequesting(now);
            Discard(slot);
            return ReassemblyResult.Gated;
        }

        FrameLease? free = null;
        foreach (FrameLease candidate in _frames)
        {
            if (!candidate.Leased)
            {
                free = candidate;
                break;
            }
        }

        if (free is null)
        {
            _gated = true;
            StartRequesting(now);
            Discard(slot);
            return ReassemblyResult.NoBuffer;
        }

        // A held frame keeps only its bytes: the slack reserved for a short or unfilled last fragment goes back.
        _rangeLength[slot.Range] = written;
        int generation = free.Lease(slot.Range);
        frame = new VideoFrame(free, generation, _pool, _rangeOffset[slot.Range], written, _epoch, number, slot.Keyframe, slot.CaptureTimeUs, now);
        slot.InUse = false;
        slot.Range = -1;
        _gated = false;
        _requesting = false;
        _inGrace = false;
        _framesComplete = unchecked(_framesComplete + 1);
        _haveDelivered = true;
        _lastDelivered = number;
        return ReassemblyResult.FrameReady;
    }

    /// <summary>Moves the fragments together when the sender didn't fill them; returns the frame's length.</summary>
    private int Compact(Slot slot)
    {
        int baseOffset = _rangeOffset[slot.Range];
        int written = 0;
        for (int i = 0; i < slot.Count; i++)
        {
            int length = slot.Lengths[i];
            int at = VideoFragmenter.DataOffset(i);
            if (at != written)
            {
                _pool.AsSpan(baseOffset + at, length).CopyTo(_pool.AsSpan(baseOffset + written, length));
            }

            written += length;
        }

        return written;
    }

    /// <summary>A complete frame that won't be handed out: counted as dropped (its fragments arrived).</summary>
    private void Discard(Slot slot)
    {
        _framesDropped = unchecked(_framesDropped + 1);
        Free(slot);
    }

    /// <summary>An incomplete frame given up: counted, remembered, and the decoder waits for a keyframe.</summary>
    private void Drop(Slot slot, long now)
    {
        _framesDropped = unchecked(_framesDropped + 1);
        _fragmentsLost = unchecked(_fragmentsLost + (uint)(slot.Count - slot.Received));
        RememberDrop(slot.Frame);
        Free(slot);
        _gated = true;
        StartRequesting(now);
    }

    private void Free(Slot slot)
    {
        if (slot.Range >= 0)
        {
            _rangeUsed[slot.Range] = false;
        }

        slot.InUse = false;
        slot.Range = -1;
    }

    private void StartRequesting(long now)
    {
        if (!_requesting || _inGrace)
        {
            _requesting = true;
            _inGrace = false;
            _requestDue = now;
        }
    }

    private void Expire(long now)
    {
        foreach (Slot slot in _slots)
        {
            if (slot.InUse && now - slot.StartedAt >= _timeoutTicks)
            {
                Drop(slot, now);
            }
        }
    }

    private void ClearSlots()
    {
        foreach (Slot slot in _slots)
        {
            if (slot.InUse)
            {
                Free(slot);
            }
        }
    }

    private Slot? Find(uint number)
    {
        foreach (Slot slot in _slots)
        {
            if (slot.InUse && slot.Frame == number)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>True for a frame dropped earlier, or one older than the drop window (too late to use).</summary>
    private bool WasDropped(uint number)
    {
        if (number < _dropBase)
        {
            return true;
        }

        uint offset = number - _dropBase;
        return offset < DropWindow && (_dropBits[offset >> 6] & (1UL << (int)(offset & 63))) != 0;
    }

    /// <summary>Remembers a dropped frame (each number once: its late fragments are stale from now on).</summary>
    private void RememberDrop(uint number)
    {
        if (number < _dropBase)
        {
            return;
        }

        uint offset = number - _dropBase;
        if (offset >= DropWindow)
        {
            uint shift = offset - DropWindow + 1;
            _dropsBelowBase = unchecked(_dropsBelowBase + ShiftDrops(shift));
            _dropBase += shift;
            offset = DropWindow - 1;
        }

        _dropBits[offset >> 6] |= 1UL << (int)(offset & 63);
    }

    /// <summary>
    /// Counts the remembered drops below <paramref name="number"/> (the frame completing now) and moves the window to
    /// start after it, keeping newer drops.
    /// </summary>
    private uint TakeDropsBelow(uint number)
    {
        uint seen = _dropsBelowBase;
        _dropsBelowBase = 0;
        if (number >= _dropBase)
        {
            // The completing frame itself is no remembered drop: a dropped frame's fragments are stale.
            seen = unchecked(seen + ShiftDrops(number - _dropBase + 1));
            _dropBase = number + 1;
        }

        return seen;
    }

    /// <summary>Shifts the window up by <paramref name="shift"/> frames; returns how many drops fell out of it.</summary>
    private uint ShiftDrops(uint shift)
    {
        if (shift == 0)
        {
            return 0;
        }

        Span<ulong> bits = _dropBits;
        if (shift >= DropWindow)
        {
            uint all = 0;
            foreach (ulong word in bits)
            {
                all += (uint)ulong.PopCount(word);
            }

            bits.Clear();
            return all;
        }

        int words = (int)(shift >> 6);
        int rest = (int)(shift & 63);
        uint count = 0;
        for (int i = 0; i < words; i++)
        {
            count += (uint)ulong.PopCount(bits[i]);
        }

        if (rest > 0)
        {
            count += (uint)ulong.PopCount(bits[words] & ((1UL << rest) - 1));
        }

        for (int i = 0; i < DropWords; i++)
        {
            int from = i + words;
            ulong low = from < DropWords ? bits[from] >> rest : 0;
            ulong high = rest > 0 && from + 1 < DropWords ? bits[from + 1] << (64 - rest) : 0;
            bits[i] = low | high;
        }

        return count;
    }

    private void ClearDrops()
    {
        Array.Clear(_dropBits);
        _dropBase = 0;
        _dropsBelowBase = 0;
    }

    private ReassemblyResult TryAllocate(uint number, int count, bool keyframe, long now, out Slot? slot)
    {
        slot = null;
        Slot? free = null;
        Slot? oldest = null;
        foreach (Slot s in _slots)
        {
            if (!s.InUse)
            {
                free ??= s;
            }
            else if (oldest is null || s.Frame < oldest.Frame)
            {
                oldest = s;
            }
        }

        if (free is null)
        {
            if (oldest is null || number < oldest.Frame)
            {
                return ReassemblyResult.NoBuffer;
            }

            Drop(oldest, now);
            free = oldest;
        }

        int need = Capacity(count);
        int range = AllocateRange(need);
        while (range < 0 && OldestBelow(number) is { } evict)
        {
            Drop(evict, now);
            range = AllocateRange(need);
        }

        if (range < 0)
        {
            return ReassemblyResult.NoBuffer;
        }

        free.Begin(number, count, keyframe, range, now);
        slot = free;
        return ReassemblyResult.Buffered;
    }

    private Slot? OldestBelow(uint number)
    {
        Slot? oldest = null;
        foreach (Slot s in _slots)
        {
            if (s.InUse && s.Frame < number && (oldest is null || s.Frame < oldest.Frame))
            {
                oldest = s;
            }
        }

        return oldest;
    }

    /// <summary>First fit in the pool; returns the range index or -1.</summary>
    private int AllocateRange(int length)
    {
        int candidate = 0;
        bool moved = true;
        while (moved)
        {
            moved = false;
            for (int i = 0; i < RangeCount; i++)
            {
                if (_rangeUsed[i] && candidate < _rangeOffset[i] + _rangeLength[i] && _rangeOffset[i] < candidate + length)
                {
                    candidate = _rangeOffset[i] + _rangeLength[i];
                    moved = true;
                }
            }

            if (candidate + length > PoolBytes)
            {
                return -1;
            }
        }

        for (int i = 0; i < RangeCount; i++)
        {
            if (!_rangeUsed[i])
            {
                _rangeUsed[i] = true;
                _rangeOffset[i] = candidate;
                _rangeLength[i] = length;
                return i;
            }
        }

        return -1;
    }

    private sealed class Slot
    {
        private readonly ulong[] _bits = new ulong[(VideoDatagram.MaxFragments + 63) / 64];

        public bool InUse { get; set; }

        public uint Frame { get; private set; }

        public int Count { get; private set; }

        public int Received { get; private set; }

        public bool Keyframe { get; private set; }

        public int FrameLength { get; set; }

        public uint CaptureTimeUs { get; set; }

        public int Range { get; set; } = -1;

        public long StartedAt { get; private set; }

        public ushort[] Lengths { get; } = new ushort[VideoDatagram.MaxFragments];

        public void Begin(uint frame, int count, bool keyframe, int range, long now)
        {
            InUse = true;
            Frame = frame;
            Count = count;
            Received = 0;
            Keyframe = keyframe;
            FrameLength = -1;
            CaptureTimeUs = 0;
            Range = range;
            StartedAt = now;
            Array.Clear(_bits);
        }

        public bool Has(int index) => (_bits[index >> 6] & (1UL << (index & 63))) != 0;

        public void Mark(int index, int length)
        {
            _bits[index >> 6] |= 1UL << (index & 63);
            Lengths[index] = (ushort)length;
            Received++;
        }
    }
}
