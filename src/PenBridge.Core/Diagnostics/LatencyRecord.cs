namespace PenBridge.Core.Diagnostics;

/// <summary>What a <see cref="LatencyRecord"/> measured.</summary>
public enum LatencyRecordKind : byte
{
    /// <summary>
    /// An accepted input frame: <see cref="LatencyRecord.ClientUs"/> is its <c>timeUs</c> (client clock, wraps at
    /// 2^32), <see cref="LatencyRecord.Server"/> when it arrived and <see cref="LatencyRecord.ServerEnd"/> when the
    /// sink (the injector) returned.
    /// </summary>
    Input = 1,

    /// <summary>
    /// A Ping from the client: <see cref="LatencyRecord.ClientUs"/> is its timestamp (client clock, same epoch as
    /// <c>timeUs</c>), <see cref="LatencyRecord.Server"/> when the server read it. One one-way sample.
    /// </summary>
    ClientPing = 2,

    /// <summary>
    /// The Pong to a server Ping: <see cref="LatencyRecord.Server"/> when the Ping was sent and
    /// <see cref="LatencyRecord.ServerEnd"/> when the Pong was read (a round trip, server clock only).
    /// </summary>
    ServerRtt = 3,

    /// <summary>
    /// A rendered video frame (screen mirroring, from a MirrorReport): <see cref="LatencyRecord.ClientUs"/> is when the
    /// phone rendered it (client clock, the Ping clock, no wrap), <see cref="LatencyRecord.Server"/> when the Mac
    /// captured it and <see cref="LatencyRecord.ServerEnd"/> when the report arrived (server clock).
    /// </summary>
    Video = 4,
}

/// <summary>Input-frame details kept with a <see cref="LatencyRecordKind.Input"/> record.</summary>
[Flags]
public enum LatencyFrameInfo : byte
{
    None = 0,

    /// <summary>Arrived as a TLS-fallback InputFrame (otherwise UDP).</summary>
    Tls = 1,

    /// <summary>The frame carried a pen sample in range.</summary>
    PenInRange = 2,

    /// <summary>The frame's pen sample was in contact.</summary>
    PenContact = 4,

    /// <summary>The frame carried at least one touch.</summary>
    Touches = 8,
}

/// <summary>
/// One latency sample. Server times are raw <see cref="TimeProvider.GetTimestamp"/> values of the server's clock;
/// client times are microseconds on the client's session clock. The two clocks are unrelated:
/// <see cref="LatencyAnalyzer"/> estimates the offset from the <see cref="LatencyRecordKind.ClientPing"/> and
/// <see cref="LatencyRecordKind.ServerRtt"/> records of the same session.
/// </summary>
public readonly record struct LatencyRecord(
    LatencyRecordKind Kind,
    LatencyFrameInfo Flags,
    uint SessionId,
    ulong ClientUs,
    long Server,
    long ServerEnd);
