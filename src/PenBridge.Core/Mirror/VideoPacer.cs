namespace PenBridge.Core.Mirror;

/// <summary>
/// Send times for the Mac's video datagrams (<c>docs/specs/screen-mirroring.md</c> "Pacing"), so a frame never goes
/// out as one burst that queues the input datagrams behind it.
/// </summary>
/// <remarks>
/// <para>Each frame is paced at <c>max(1.5 × bitrate, frame bytes / window)</c>: never faster than the 1.5 × bitrate
/// token rate unless that's needed to finish within the window, which is half the frame interval (a keyframe: one
/// interval). An average frame (bitrate / fps bytes) would need two thirds of an interval at the token rate, so for
/// most frames the window sets the rate; the token rate keeps small frames from going out as a burst.</para>
/// <para>Usage on the send thread: <see cref="Configure"/> on each MirrorConfig; per frame <see cref="BeginFrame"/>,
/// then for each datagram wait <see cref="GetDelay"/> (send at once when zero) and call <see cref="OnSent"/>. A wake-up
/// later than asked may catch up by up to <see cref="Slack"/> worth of datagrams, then pacing resumes; idle time between
/// frames earns no burst. Not thread-safe;
/// allocation-free.</para>
/// </remarks>
public sealed class VideoPacer
{
    /// <summary>The token rate as a multiple of the target bitrate.</summary>
    public const double RateFactor = 1.5;

    /// <summary>How far a late sender may catch up in one burst.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(1);

    private readonly TimeProvider _time;
    private double _bucketBytesPerTick;
    private double _bytesPerTick;
    private long _intervalTicks;
    private double _nextAt;
    private bool _configured;

    public VideoPacer(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>The frame interval of the configured fps.</summary>
    public TimeSpan FrameInterval => _configured ? TimeSpan.FromSeconds((double)_intervalTicks / _time.TimestampFrequency) : TimeSpan.Zero;

    /// <summary>Sets the stream's bitrate and fps (each MirrorConfig).</summary>
    public void Configure(int bitrateKbps, int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitrateKbps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        double frequency = _time.TimestampFrequency;
        _bucketBytesPerTick = RateFactor * bitrateKbps * 1000 / 8 / frequency;
        _bytesPerTick = _bucketBytesPerTick;
        _intervalTicks = (long)(frequency / fps);
        _configured = true;
    }

    /// <summary>How long a frame may take to go out: half the interval, a keyframe one interval.</summary>
    public TimeSpan Window(bool keyframe) => keyframe ? FrameInterval : FrameInterval / 2;

    /// <summary>Starts pacing a frame of <paramref name="totalDatagramBytes"/> (every datagram's full size).</summary>
    public void BeginFrame(int totalDatagramBytes, bool keyframe)
    {
        if (!_configured)
        {
            throw new InvalidOperationException("Configure the pacer first.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(totalDatagramBytes, 1);
        long window = keyframe ? _intervalTicks : _intervalTicks / 2;
        _bytesPerTick = Math.Max(_bucketBytesPerTick, totalDatagramBytes / (double)Math.Max(1, window));

        // Idle time before a frame earns no burst; only a late wake-up inside a frame may catch up (Slack).
        _nextAt = Math.Max(_nextAt, _time.GetTimestamp());
    }

    /// <summary>How long to wait before the next datagram, rounded up to whole ticks; <see cref="TimeSpan.Zero"/> means send now.</summary>
    public TimeSpan GetDelay()
    {
        double wait = _nextAt - _time.GetTimestamp();
        return wait <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)Math.Ceiling(wait * TimeSpan.TicksPerSecond / _time.TimestampFrequency));
    }

    /// <summary>Records a datagram of <paramref name="datagramBytes"/> just sent.</summary>
    public void OnSent(int datagramBytes)
    {
        if (!_configured)
        {
            throw new InvalidOperationException("Configure the pacer first.");
        }

        double now = _time.GetTimestamp();
        double earliest = now - (Slack.TotalSeconds * _time.TimestampFrequency);
        _nextAt = Math.Max(_nextAt, earliest) + (datagramBytes / _bytesPerTick);
    }
}
