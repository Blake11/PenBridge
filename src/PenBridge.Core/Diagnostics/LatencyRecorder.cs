namespace PenBridge.Core.Diagnostics;

/// <summary>
/// A fixed-size ring buffer of <see cref="LatencyRecord"/>s filled by the server's input path when latency
/// diagnostics are on (<see cref="Net.PenBridgeServerOptions.LatencyRecorder"/>; null = off, the default).
/// </summary>
/// <remarks>
/// Recording is allocation-free and formats nothing: the buffer is allocated once and the oldest records are
/// overwritten when it is full. A host reads it on demand or at session end with <see cref="Snapshot"/> and writes it
/// with <see cref="LatencyDump"/>. Thread-safe: the UDP loop, the control read loops and a dumping thread may use it
/// concurrently (one short uncontended lock per record).
/// </remarks>
public sealed class LatencyRecorder
{
    /// <summary>Environment variable that turns latency diagnostics on in the heads when set to <c>1</c>.</summary>
    public const string EnvironmentVariable = "PENBRIDGE_LATENCY_LOG";

    /// <summary>About 9 minutes of input at 240 frames/s (32 B per record, 4 MB).</summary>
    public const int DefaultCapacity = 1 << 17;

    private readonly Lock _lock = new();
    private readonly LatencyRecord[] _ring;
    private long _total;

    public LatencyRecorder(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new LatencyRecord[capacity];
    }

    /// <summary>True when <see cref="EnvironmentVariable"/> is <c>1</c> (or <c>true</c>).</summary>
    public static bool IsEnabledByEnvironment =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true" or "TRUE" or "True";

    public int Capacity => _ring.Length;

    /// <summary>Records written since creation or the last <see cref="Clear"/>, including overwritten ones.</summary>
    public long Total
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
    }

    /// <summary>Records currently held (at most <see cref="Capacity"/>).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return (int)Math.Min(_total, _ring.Length);
            }
        }
    }

    /// <summary>An accepted input frame: arrival and sink-returned timestamps (server clock) and its <c>timeUs</c>.</summary>
    public void RecordInput(uint sessionId, uint clientTimeUs, long received, long injected, LatencyFrameInfo flags) =>
        Add(new LatencyRecord(LatencyRecordKind.Input, flags, sessionId, clientTimeUs, received, injected));

    /// <summary>A client Ping read by the server: the Ping's timestamp (client clock) and when it was read.</summary>
    public void RecordClientPing(uint sessionId, ulong clientTimeUs, long received) =>
        Add(new LatencyRecord(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, sessionId, clientTimeUs, received, received));

    /// <summary>A server Ping's round trip, both ends on the server clock.</summary>
    public void RecordServerRtt(uint sessionId, long sent, long received) =>
        Add(new LatencyRecord(LatencyRecordKind.ServerRtt, LatencyFrameInfo.None, sessionId, 0, sent, received));

    /// <summary>A video frame the phone rendered at <paramref name="renderedAtUs"/> (client clock), captured at <paramref name="captured"/> (server clock); <paramref name="reported"/> is when its MirrorReport arrived.</summary>
    public void RecordVideo(uint sessionId, ulong renderedAtUs, long captured, long reported) =>
        Add(new LatencyRecord(LatencyRecordKind.Video, LatencyFrameInfo.None, sessionId, renderedAtUs, captured, reported));

    /// <summary>Records that may arrive between sizing a snapshot and copying it.</summary>
    private const int SnapshotSlack = 4096;

    /// <summary>
    /// The held records, oldest first; with <paramref name="clear"/> they are also dropped (atomically). The copy is
    /// allocated before taking the lock, so recording never waits on a multi-MB allocation; if more than
    /// <see cref="SnapshotSlack"/> records arrive in between, only the newest that fit are copied.
    /// </summary>
    public LatencyRecord[] Snapshot(bool clear = false)
    {
        var copy = new LatencyRecord[Math.Min(Count + SnapshotSlack, _ring.Length)];
        int count;
        lock (_lock)
        {
            count = (int)Math.Min(Math.Min(_total, _ring.Length), copy.Length);
            int start = (int)((_total - count) % _ring.Length);
            int first = Math.Min(count, _ring.Length - start);
            Array.Copy(_ring, start, copy, 0, first);
            Array.Copy(_ring, 0, copy, first, count - first);
            if (clear)
            {
                _total = 0;
            }
        }

        if (count < copy.Length)
        {
            Array.Resize(ref copy, count);
        }

        return copy;
    }

    /// <summary>Drops every held record.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _total = 0;
        }
    }

    private void Add(in LatencyRecord record)
    {
        lock (_lock)
        {
            _ring[(int)(_total % _ring.Length)] = record;
            _total++;
        }
    }
}
