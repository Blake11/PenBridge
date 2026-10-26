namespace PenBridge.Core.Diagnostics;

/// <summary>Order statistics of a set of latencies, in microseconds. All NaN when <see cref="Count"/> is 0.</summary>
public readonly record struct LatencyStats(int Count, double MinUs, double P50Us, double P95Us, double MaxUs)
{
    public static LatencyStats Empty { get; } = new(0, double.NaN, double.NaN, double.NaN, double.NaN);

    /// <summary>Sorts <paramref name="values"/> in place and takes nearest-rank percentiles.</summary>
    public static LatencyStats From(Span<double> values)
    {
        if (values.IsEmpty)
        {
            return Empty;
        }

        values.Sort();
        return new LatencyStats(values.Length, values[0], Percentile(values, 50), Percentile(values, 95), values[^1]);
    }

    /// <summary>Nearest-rank percentile of sorted values: the smallest value with at least p % of the values at or below it.</summary>
    public static double Percentile(ReadOnlySpan<double> sorted, double percent)
    {
        if (sorted.IsEmpty)
        {
            return double.NaN;
        }

        int rank = (int)Math.Ceiling(percent / 100 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}

/// <summary>
/// The clock model of one session: server time ≈ client time + <see cref="OffsetUs"/> + drift × elapsed. Server times
/// are microseconds from the dump's first record.
/// </summary>
/// <param name="OffsetUs">Server minus client clock at <see cref="ReferenceUs"/> (NaN without client Pings).</param>
/// <param name="ReferenceUs">Server time of the session's first client Ping.</param>
/// <param name="DriftPpm">Rate difference of the two clocks (0 when the Pings span less than <see cref="LatencyAnalyzer.MinDriftSpan"/>).</param>
/// <param name="MinRttUs">Smallest server Ping round trip (NaN without one).</param>
/// <param name="UncertaintyUs">Bound on the offset error: ±half the smallest round trip (NaN without one).</param>
public readonly record struct SessionClock(
    uint SessionId,
    int ClientPings,
    int RoundTrips,
    int Inputs,
    double OffsetUs,
    double ReferenceUs,
    double DriftPpm,
    double MinRttUs,
    double UncertaintyUs)
{
    public bool HasOffset => !double.IsNaN(OffsetUs);
}

/// <summary>The result of <see cref="LatencyAnalyzer.Analyze"/>; latencies are offset-corrected, in microseconds.</summary>
/// <param name="Receive">Client send (<c>timeUs</c>) → the server read the frame.</param>
/// <param name="Inject">Client send → the sink (injector) returned, every accepted frame.</param>
/// <param name="InjectContact">Same, frames with the pen in contact.</param>
/// <param name="PenDown">Same, only the first contact frame of each stroke (pen-down → Mac event).</param>
/// <param name="UnmeasuredInputs">Input frames of sessions without a client Ping (no offset estimate).</param>
/// <param name="NegativeReceive">Frames whose corrected receive latency came out below zero (offset error).</param>
public sealed record LatencyReport(
    IReadOnlyList<SessionClock> Sessions,
    LatencyStats Receive,
    LatencyStats Inject,
    LatencyStats InjectContact,
    LatencyStats PenDown,
    int UnmeasuredInputs,
    int NegativeReceive)
{
    /// <summary>Screen mirroring: Mac capture → phone render (offset-corrected), one sample per reported frame.</summary>
    public LatencyStats Video { get; init; } = LatencyStats.Empty;

    /// <summary>Video samples of sessions without a client Ping (no offset estimate).</summary>
    public int UnmeasuredVideo { get; init; }
}

/// <summary>
/// Turns <see cref="LatencyRecorder"/> records into offset-corrected latency statistics, using only what the wire
/// already carries (no protocol change).
/// </summary>
/// <remarks>
/// <para>Clock offset θ = server − client, estimated per session, NTP style with min filtering:</para>
/// <list type="bullet">
/// <item>Each client Ping gives a one-way sample <c>o = t1 − t0</c> = θ + d_up (t0: the Ping's client timestamp,
/// t1: when the server read it), so the lower envelope of the samples is θ + min(d_up).</item>
/// <item>The server's own Pings give round trips; with symmetric minimum delays, min(d_up) ≈ minRTT / 2. Hence
/// θ ≈ min(o) − minRTT / 2 with an error within ±minRTT / 2 (the same bound as NTP's
/// <c>((t1 − t0) + (t2 − t3)) / 2</c> on its min-RTT exchange; Pong only echoes the Ping, so the four timestamps
/// of one exchange are not on one side).</item>
/// <item>Clock drift (tens of ppm, i.e. milliseconds over a long session) is removed by fitting the slope through
/// the minima of <see cref="DriftWindow"/>-long windows (taken on detrended values, refined over a few rounds), then
/// shifting that line down to the lowest sample.</item>
/// </list>
/// <para>Input <c>timeUs</c> (u32, wraps after about 71 minutes) is unwrapped against the fitted clock model, since
/// Pings and input frames share the client's session epoch.</para>
/// </remarks>
public static class LatencyAnalyzer
{
    /// <summary>Window whose minimum one-way sample counts as one point of the drift fit.</summary>
    public static readonly TimeSpan DriftWindow = TimeSpan.FromSeconds(30);

    /// <summary>Pings must span at least this long before drift is estimated.</summary>
    public static readonly TimeSpan MinDriftSpan = TimeSpan.FromSeconds(60);

    private const double Wrap = 4294967296.0; // 2^32 µs

    private const int FitRounds = 4;

    public static LatencyReport Analyze(ReadOnlySpan<LatencyRecord> records, long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timestampFrequency, 1);
        long origin = Origin(records);
        var sessions = new List<SessionData>();
        var byId = new Dictionary<uint, SessionData>();
        foreach (ref readonly LatencyRecord r in records)
        {
            if (!byId.TryGetValue(r.SessionId, out SessionData? s))
            {
                s = new SessionData(r.SessionId);
                byId.Add(r.SessionId, s);
                sessions.Add(s);
            }

            switch (r.Kind)
            {
                case LatencyRecordKind.ClientPing:
                    double t1 = ToUs(r.Server, origin, timestampFrequency);
                    s.PingTimes.Add(t1);
                    s.OneWay.Add(t1 - r.ClientUs);
                    break;
                case LatencyRecordKind.ServerRtt:
                    double rtt = ToUs(r.ServerEnd, origin, timestampFrequency) - ToUs(r.Server, origin, timestampFrequency);
                    if (rtt >= 0)
                    {
                        s.MinRtt = Math.Min(s.MinRtt, rtt);
                        s.RoundTrips++;
                    }

                    break;
                case LatencyRecordKind.Input:
                    s.Inputs.Add(r);
                    break;
                case LatencyRecordKind.Video:
                    s.Videos.Add(r);
                    break;
            }
        }

        var clocks = new List<SessionClock>(sessions.Count);
        var receive = new List<double>();
        var inject = new List<double>();
        var contact = new List<double>();
        var down = new List<double>();
        var video = new List<double>();
        int unmeasuredVideo = 0;
        int unmeasured = 0;
        int negative = 0;
        foreach (SessionData s in sessions)
        {
            bool hasRtt = s.RoundTrips > 0;
            double minRtt = hasRtt ? s.MinRtt : double.NaN;
            if (s.OneWay.Count == 0)
            {
                unmeasured += s.Inputs.Count;
                unmeasuredVideo += s.Videos.Count;
                clocks.Add(new SessionClock(s.Id, 0, s.RoundTrips, s.Inputs.Count, double.NaN, double.NaN, 0, minRtt, double.NaN));
                continue;
            }

            ClockLine line = FitLowerLine(s.PingTimes, s.OneWay, DriftWindow.TotalMicroseconds, MinDriftSpan.TotalMicroseconds);
            double correction = hasRtt ? minRtt / 2 : 0;
            clocks.Add(new SessionClock(
                s.Id,
                s.OneWay.Count,
                s.RoundTrips,
                s.Inputs.Count,
                line.Intercept - correction,
                line.Reference,
                line.Slope * 1e6,
                minRtt,
                hasRtt ? minRtt / 2 : double.NaN));

            bool wasContact = false;
            foreach (LatencyRecord r in s.Inputs)
            {
                double received = ToUs(r.Server, origin, timestampFrequency);
                double injected = ToUs(r.ServerEnd, origin, timestampFrequency);
                double theta = line.At(received) - correction;
                double sent = Unwrap((uint)r.ClientUs, received - line.At(received)) + theta;
                double rx = received - sent;
                double total = injected - sent;
                receive.Add(rx);
                inject.Add(total);
                if (rx < 0)
                {
                    negative++;
                }

                bool isContact = (r.Flags & LatencyFrameInfo.PenContact) != 0;
                if (isContact)
                {
                    contact.Add(total);
                    if (!wasContact)
                    {
                        down.Add(total);
                    }
                }

                wasContact = isContact;
            }

            foreach (LatencyRecord r in s.Videos)
            {
                // Rendered on the phone clock (u64, no wrap), mapped with the offset at the report's arrival.
                double reported = ToUs(r.ServerEnd, origin, timestampFrequency);
                double rendered = r.ClientUs + line.At(reported) - correction;
                video.Add(rendered - ToUs(r.Server, origin, timestampFrequency));
            }
        }

        return new LatencyReport(
            clocks,
            LatencyStats.From(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(receive)),
            LatencyStats.From(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inject)),
            LatencyStats.From(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(contact)),
            LatencyStats.From(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(down)),
            unmeasured,
            negative)
        {
            Video = LatencyStats.From(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(video)),
            UnmeasuredVideo = unmeasuredVideo,
        };
    }

    /// <summary>The earliest server timestamp: server times in a report are microseconds from it.</summary>
    public static long Origin(ReadOnlySpan<LatencyRecord> records)
    {
        long origin = long.MaxValue;
        foreach (ref readonly LatencyRecord r in records)
        {
            origin = Math.Min(origin, Math.Min(r.Server, r.ServerEnd));
        }

        return records.IsEmpty ? 0 : origin;
    }

    /// <summary>A server timestamp as microseconds from <paramref name="origin"/>.</summary>
    public static double ToUs(long timestamp, long origin, long frequency)
    {
        long delta = timestamp - origin;
        long whole = delta / frequency;
        long rest = delta % frequency;
        return (whole * 1e6) + (rest * 1e6 / frequency);
    }

    /// <summary>The u32 <paramref name="raw"/> plus the multiple of 2^32 that lands closest to <paramref name="approximate"/>.</summary>
    internal static double Unwrap(uint raw, double approximate)
    {
        double k = Math.Max(0, Math.Round((approximate - raw) / Wrap));
        return raw + (k * Wrap);
    }

    /// <summary>
    /// The lower envelope <c>v ≈ a + b (t − t_ref)</c> of (time, value) samples: slope from the per-window minima
    /// (zero when the samples span less than <paramref name="minSpan"/>), intercept from the lowest sample.
    /// </summary>
    internal static ClockLine FitLowerLine(IReadOnlyList<double> times, IReadOnlyList<double> values, double window, double minSpan)
    {
        double t0 = double.MaxValue;
        double t1 = double.MinValue;
        for (int i = 0; i < times.Count; i++)
        {
            t0 = Math.Min(t0, times[i]);
            t1 = Math.Max(t1, times[i]);
        }

        double slope = 0;
        if (t1 - t0 >= minSpan)
        {
            // Each window's minimum is taken on the detrended values: with drift, a raw minimum leans toward the
            // window's early (or late) samples. A few rounds converge.
            var minima = new SortedDictionary<long, (double T, double V)>();
            for (int round = 0; round < FitRounds; round++)
            {
                minima.Clear();
                for (int i = 0; i < times.Count; i++)
                {
                    long w = (long)((times[i] - t0) / window);
                    double detrended = values[i] - (slope * (times[i] - t0));
                    if (!minima.TryGetValue(w, out (double T, double V) m) || detrended < m.V - (slope * (m.T - t0)))
                    {
                        minima[w] = (times[i], values[i]);
                    }
                }

                if (minima.Count < 2)
                {
                    break;
                }

                slope = Slope(minima.Values);
            }
        }

        double intercept = double.MaxValue;
        for (int i = 0; i < times.Count; i++)
        {
            intercept = Math.Min(intercept, values[i] - (slope * (times[i] - t0)));
        }

        return new ClockLine(t0, intercept, slope);
    }

    private static double Slope(IEnumerable<(double T, double V)> points)
    {
        double n = 0;
        double meanT = 0;
        double meanV = 0;
        foreach ((double t, double v) in points)
        {
            n++;
            meanT += t;
            meanV += v;
        }

        meanT /= n;
        meanV /= n;
        double sxy = 0;
        double sxx = 0;
        foreach ((double t, double v) in points)
        {
            sxy += (t - meanT) * (v - meanV);
            sxx += (t - meanT) * (t - meanT);
        }

        return sxx > 0 ? sxy / sxx : 0;
    }

    internal readonly record struct ClockLine(double Reference, double Intercept, double Slope)
    {
        public double At(double t) => Intercept + (Slope * (t - Reference));
    }

    private sealed class SessionData(uint id)
    {
        public uint Id { get; } = id;

        public List<double> PingTimes { get; } = [];

        public List<double> OneWay { get; } = [];

        public List<LatencyRecord> Inputs { get; } = [];

        public List<LatencyRecord> Videos { get; } = [];

        public double MinRtt { get; set; } = double.MaxValue;

        public int RoundTrips { get; set; }
    }
}
