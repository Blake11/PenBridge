using PenBridge.Core.Mirror;

namespace PenBridge.Core.Tests.Mirror;

/// <summary>docs/specs/screen-mirroring.md "Pacing", with a fake clock (10 MHz timestamps).</summary>
public class VideoPacerTests
{
    private readonly FakeTime _time = new();
    private readonly VideoPacer _pacer;

    public VideoPacerTests()
    {
        _pacer = new VideoPacer(_time);
        _pacer.Configure(12_000, 60);
    }

    [Fact]
    public void Windows_are_half_an_interval_and_a_full_one_for_keyframes()
    {
        Assert.Equal(TimeSpan.FromTicks(166_666), _pacer.FrameInterval);
        Assert.Equal(TimeSpan.FromTicks(83_333), _pacer.Window(false));
        Assert.Equal(TimeSpan.FromTicks(166_666), _pacer.Window(true));
    }

    [Fact]
    public void A_large_frame_is_spread_over_half_the_interval()
    {
        // 30 kB at 60 fps: 3.6 MB/s to finish in 8.33 ms, above the 2.25 MB/s token rate.
        List<long> sends = SendFrame(25, keyframe: false);
        Assert.Equal(0, sends[0]);
        Assert.InRange(sends[1] - sends[0], 3_333, 3_334);
        Assert.InRange(sends[^1], 79_000, 83_333);
    }

    [Fact]
    public void A_small_frame_goes_at_the_token_rate()
    {
        // 1.5 × 12 Mbps = 2.25 MB/s: one 1200-byte datagram per 533 µs.
        List<long> sends = SendFrame(3, keyframe: false);
        Assert.Equal([0, 5_334, 10_667], sends);
    }

    [Fact]
    public void A_keyframe_may_take_the_whole_interval()
    {
        // 300 kB: 18 MB/s to finish in 16.7 ms.
        List<long> sends = SendFrame(250, keyframe: true);
        Assert.InRange(sends[1] - sends[0], 666, 667);
        Assert.InRange(sends[^1], 160_000, 166_666);
    }

    [Fact]
    public void A_late_wake_up_catches_up_by_at_most_one_millisecond()
    {
        _pacer.BeginFrame(25 * 1200, keyframe: false);
        _pacer.OnSent(1200);
        _time.Advance(TimeSpan.FromMilliseconds(3)); // woke 2.67 ms late
        int burst = 0;
        while (_pacer.GetDelay() == TimeSpan.Zero && burst < 10)
        {
            _pacer.OnSent(1200);
            burst++;
        }

        Assert.Equal(4, burst); // the one that was due, then 1 ms at 333 µs each
        Assert.InRange(_pacer.GetDelay(), TimeSpan.FromTicks(1), TimeSpan.FromTicks(3_334));
    }

    [Fact]
    public void Idle_time_between_frames_earns_no_burst()
    {
        SendFrame(3, keyframe: false);
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _pacer.BeginFrame(3 * 1200, keyframe: false);
        Assert.Equal(TimeSpan.Zero, _pacer.GetDelay());
        _pacer.OnSent(1200);
        Assert.Equal(TimeSpan.FromTicks(5_334), _pacer.GetDelay());
    }

    [Fact]
    public void Reconfiguring_changes_the_rate()
    {
        _pacer.Configure(24_000, 120);
        Assert.Equal(TimeSpan.FromTicks(83_333), _pacer.FrameInterval);
        _pacer.BeginFrame(2400, keyframe: false);
        _pacer.OnSent(1200);
        Assert.Equal(TimeSpan.FromTicks(2_667), _pacer.GetDelay()); // 4.5 MB/s
    }

    [Fact]
    public void Misuse_throws()
    {
        var pacer = new VideoPacer(_time);
        Assert.Equal(TimeSpan.Zero, pacer.FrameInterval);
        Assert.Throws<InvalidOperationException>(() => pacer.BeginFrame(1200, false));
        Assert.Throws<InvalidOperationException>(() => pacer.OnSent(1200));
        Assert.Throws<ArgumentOutOfRangeException>(() => pacer.Configure(0, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => pacer.Configure(1000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _pacer.BeginFrame(0, false));
        Assert.Throws<ArgumentNullException>(() => new VideoPacer(null!));
    }

    /// <summary>Sends a frame of 1200-byte datagrams as the send thread would; returns each send's tick offset.</summary>
    private List<long> SendFrame(int datagrams, bool keyframe)
    {
        long start = _time.GetTimestamp();
        var sends = new List<long>();
        _pacer.BeginFrame(datagrams * 1200, keyframe);
        for (int i = 0; i < datagrams; i++)
        {
            TimeSpan delay = _pacer.GetDelay();
            if (delay > TimeSpan.Zero)
            {
                _time.Advance(delay);
            }

            Assert.Equal(TimeSpan.Zero, _pacer.GetDelay());
            sends.Add(_time.GetTimestamp() - start);
            _pacer.OnSent(1200);
        }

        return sends;
    }
}
