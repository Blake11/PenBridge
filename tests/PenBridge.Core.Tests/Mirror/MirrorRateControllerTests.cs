using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Mirror;

/// <summary>The input guard: docs/specs/screen-mirroring.md "QoS" (ladder, recovery), with a fake clock.</summary>
public class MirrorRateControllerTests
{
    private static readonly MirrorStreamPlan Plan = new(MirrorCodec.Hevc, 2228, 1440, 120, 20000, 5000);

    private readonly FakeTime _time = new();
    private readonly MirrorRateController _guard;

    public MirrorRateControllerTests()
    {
        _guard = new MirrorRateController(_time);
        _guard.Start(Plan);
    }

    [Fact]
    public void Starts_at_full_quality_and_stays_there_while_healthy()
    {
        Assert.Equal(new MirrorGuardLevel(20000, 120, false), _guard.Level);
        Rtt(10, 10, 10);
        Assert.False(_guard.Update());
        Assert.Null(_guard.NextUpdateIn);
        Assert.False(_guard.IsDegraded);
        Assert.Equal(TimeSpan.FromMilliseconds(10), _guard.RttBaseline);
    }

    [Fact]
    public void Two_consecutive_high_rtt_samples_degrade()
    {
        Rtt(10, 10);
        Rtt(19);
        Assert.False(_guard.IsDegraded); // one sample is noise
        Rtt(10, 19);
        Assert.False(_guard.IsDegraded); // not consecutive
        Rtt(18, 18);
        Assert.False(_guard.IsDegraded); // exactly baseline + 8 ms is fine
        Rtt(19);
        Assert.False(_guard.IsDegraded);
        Rtt(19);
        Assert.True(_guard.IsDegraded);
        Assert.True(_guard.Update());
        Assert.Equal(new MirrorGuardLevel(14000, 120, false), _guard.Level);
    }

    [Fact]
    public void The_ladder_steps_bitrate_then_fps_then_pauses_once_per_500_ms()
    {
        Degrade();
        MirrorGuardLevel[] expected =
        [
            new(14000, 120, false),
            new(9800, 120, false),
            new(6860, 120, false),
            new(5000, 120, false), // down to the floor
            new(5000, 60, false),  // fps halves
            new(5000, 30, false),
            new(5000, 30, true),   // then the video pauses
        ];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(_guard.Update(), $"step {i}");
            Assert.Equal(expected[i], _guard.Level);
            Assert.False(_guard.Update()); // one step per interval
            Assert.Equal(i == expected.Length - 1 ? null : TimeSpan.FromMilliseconds(500), _guard.NextUpdateIn);
            _time.Advance(TimeSpan.FromMilliseconds(499));
            Assert.False(_guard.Update());
            _time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.False(_guard.Update()); // paused: nothing further
        Assert.Equal(new MirrorGuardLevel(5000, 30, true), _guard.Level);
    }

    [Fact]
    public void Recovery_waits_2_s_then_steps_up_every_second_fps_first()
    {
        Degrade();
        while (_guard.Update() || !_guard.Level.Paused)
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
        }

        Rtt(10); // healthy again
        Assert.False(_guard.IsDegraded);
        Assert.Equal(TimeSpan.FromSeconds(2), _guard.NextUpdateIn);
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(_guard.Update());
        _time.Advance(TimeSpan.FromMilliseconds(1));

        MirrorGuardLevel[] expected =
        [
            new(5000, 30, false), // unpause
            new(5000, 60, false), // fps back first
            new(5000, 120, false),
            new(5750, 120, false), // then bitrate × 1.15
            new(6612, 120, false),
            new(7604, 120, false),
            new(8745, 120, false),
            new(10057, 120, false),
            new(11566, 120, false),
            new(13301, 120, false),
            new(15296, 120, false),
            new(17590, 120, false),
            new(20000, 120, false), // up to the target
        ];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(_guard.Update(), $"step {i}");
            Assert.Equal(expected[i], _guard.Level);
            Assert.False(_guard.Update());
            if (i < expected.Length - 1)
            {
                Assert.Equal(TimeSpan.FromSeconds(1), _guard.NextUpdateIn);
                _time.Advance(TimeSpan.FromMilliseconds(999));
                Assert.False(_guard.Update());
                _time.Advance(TimeSpan.FromMilliseconds(1));
            }
        }

        Assert.Null(_guard.NextUpdateIn);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_guard.Update());
    }

    [Fact]
    public void A_new_degradation_during_recovery_steps_down_at_once()
    {
        Degrade();
        Assert.True(_guard.Update()); // 14000
        Rtt(10);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(_guard.Update()); // back to 16100
        Assert.Equal(16100, _guard.Level.BitrateKbps);
        _guard.OnVideoLoss(10, 100);
        Assert.True(_guard.Update());
        Assert.Equal(11270, _guard.Level.BitrateKbps);
    }

    [Fact]
    public void An_input_gap_during_contact_degrades_for_one_step()
    {
        uint t = 1_000_000;
        ulong seq = 1;
        Assert.False(_guard.OnInputDatagram(seq, true, t));
        for (int i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(40)); // a still pen: 40 ms contact keepalives
            Assert.False(_guard.OnInputDatagram(++seq, true, t += 40_000));
        }

        Assert.False(_guard.IsDegraded);
        _time.Advance(TimeSpan.FromMilliseconds(70)); // 30 ms late: not yet a gap
        Assert.False(_guard.OnInputDatagram(++seq, true, t += 40_000));
        Assert.False(_guard.IsDegraded);

        _time.Advance(TimeSpan.FromMilliseconds(71)); // 31 ms late: a new degradation
        Assert.True(_guard.OnInputDatagram(++seq, true, t += 40_000));
        Assert.True(_guard.IsDegraded);
        Assert.True(_guard.Update());
        Assert.Equal(14000, _guard.Level.BitrateKbps);
        Assert.Equal(TimeSpan.FromMilliseconds(500), _guard.NextUpdateIn);

        _time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(_guard.IsDegraded); // the signal lasted one interval
        Assert.False(_guard.Update());
        Assert.Equal(TimeSpan.FromSeconds(2), _guard.NextUpdateIn);
    }

    [Fact]
    public void Gaps_count_only_while_in_contact()
    {
        Assert.False(_guard.OnInputDatagram(1, false, 0));
        _time.Advance(TimeSpan.FromMilliseconds(500)); // hover pause
        Assert.False(_guard.OnInputDatagram(2, true, 10_000)); // pen down: the previous wasn't in contact
        Assert.False(_guard.IsDegraded);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(_guard.OnInputDatagram(3, true, 5_000)); // timeUs backwards: no lateness check
        Assert.False(_guard.IsDegraded);

        _time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.False(_guard.OnInputDatagram(4, false, 15_000)); // the lift arrives in step with its time
        Assert.False(_guard.IsDegraded);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(_guard.OnInputDatagram(5, true, 25_000)); // after a lift nothing is late
        Assert.False(_guard.IsDegraded);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(_guard.OnInputDatagram(6, false, 25_000)); // a resend (same time) 100 ms later: a gap
        Assert.True(_guard.IsDegraded);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]   // lost before a lift
    [InlineData(false, true, true)]   // the pen-down may be among the lost
    [InlineData(false, false, false)] // hover only
    public void Lost_input_datagrams_around_a_contact_degrade(bool before, bool after, bool degraded)
    {
        Assert.False(_guard.OnInputDatagram(10, before, 1_000));
        _time.Advance(TimeSpan.FromMilliseconds(8)); // in step with timeUs: only the sequence gap counts
        Assert.Equal(degraded, _guard.OnInputDatagram(12, after, 9_000));
        Assert.Equal(degraded, _guard.IsDegraded);
        if (degraded)
        {
            Assert.True(_guard.Update());
            Assert.Equal(14000, _guard.Level.BitrateKbps); // the same ladder
        }
    }

    [Fact]
    public void Only_the_start_of_an_input_degradation_returns_true()
    {
        ulong seq = 1;
        Assert.False(_guard.OnInputDatagram(seq, true, 0));
        Assert.True(_guard.OnInputDatagram(seq += 2, true, 0));
        _time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.False(_guard.OnInputDatagram(seq += 2, true, 400_000)); // extends the active signal
        _time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.True(_guard.IsDegraded);                                 // extended to 900 ms
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(_guard.IsDegraded);
        Assert.True(_guard.OnInputDatagram(seq += 2, true, 900_000));  // over: a new degradation
    }

    [Fact]
    public void A_sequence_that_starts_over_resets_without_a_signal()
    {
        Assert.False(_guard.OnInputDatagram(500, true, 0));
        _time.Advance(TimeSpan.FromMilliseconds(4));
        Assert.False(_guard.OnInputDatagram(1, true, 4_000)); // a new session's sequence space
        _time.Advance(TimeSpan.FromMilliseconds(4));
        Assert.False(_guard.OnInputDatagram(2, true, 8_000));
        Assert.False(_guard.IsDegraded);
    }

    [Fact]
    public void Loss_above_2_percent_degrades()
    {
        _guard.OnVideoLoss(2, 100);
        Assert.False(_guard.IsDegraded);
        _guard.OnVideoLoss(0, 0);
        _guard.OnVideoLoss(5, 0);
        Assert.False(_guard.IsDegraded);
        _guard.OnVideoLoss(21, 1000);
        Assert.True(_guard.IsDegraded);
    }

    [Fact]
    public void Reports_count_loss_since_the_previous_report_of_the_epoch()
    {
        _guard.OnMirrorReport(Report(epoch: 1, lost: 1), currentEpoch: 1, fragmentsSentInEpoch: 100);
        Assert.False(_guard.IsDegraded); // 1 %
        _guard.OnMirrorReport(Report(epoch: 1, lost: 4), currentEpoch: 1, fragmentsSentInEpoch: 200);
        Assert.True(_guard.IsDegraded); // 3 of 100 since the previous report

        _time.Advance(TimeSpan.FromSeconds(1));
        _guard.OnMirrorReport(Report(epoch: 0, lost: 1000), currentEpoch: 1, fragmentsSentInEpoch: 300);
        Assert.False(_guard.IsDegraded); // another epoch's report is ignored

        _guard.OnMirrorReport(Report(epoch: 2, lost: 1), currentEpoch: 2, fragmentsSentInEpoch: 100);
        Assert.False(_guard.IsDegraded); // a new epoch counts from its config

        _guard.OnMirrorReport(Report(epoch: 2, lost: uint.MaxValue - 1), currentEpoch: 2, fragmentsSentInEpoch: 200);
        _time.Advance(TimeSpan.FromSeconds(1));
        _guard.OnMirrorReport(Report(epoch: 2, lost: 2), currentEpoch: 2, fragmentsSentInEpoch: 400);
        Assert.False(_guard.IsDegraded); // the counter wrapped: 4 of 200 since the previous report
        _guard.OnMirrorReport(Report(epoch: 2, lost: 9), currentEpoch: 2, fragmentsSentInEpoch: 600);
        Assert.True(_guard.IsDegraded); // 7 of 200

        static MirrorReportMessage Report(byte epoch, uint lost) => new(epoch, 10, 0, lost, 9, 0);
    }

    [Fact]
    public void The_rtt_baseline_is_the_minimum_over_10_s()
    {
        Rtt(5);
        _time.Advance(TimeSpan.FromSeconds(5));
        Rtt(20);
        Assert.Equal(TimeSpan.FromMilliseconds(5), _guard.RttBaseline);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(5), _guard.RttBaseline); // exactly 10 s old: still in
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(20), _guard.RttBaseline);
        Rtt(27, 27);
        Assert.False(_guard.IsDegraded); // the old 5 ms no longer counts
        Rtt(29, 29);
        Assert.True(_guard.IsDegraded);
        _guard.OnRttSample(TimeSpan.FromMilliseconds(-1)); // ignored
        Assert.True(_guard.IsDegraded);
    }

    [Theory]
    [InlineData(4, 10_000)]  // exact: one slot per sample
    [InlineData(10, 10_000)]
    [InlineData(50, 10_080)] // bucketed: the 0 ms minimum shares its slot with the samples up to 80 ms
    public void The_rtt_baseline_covers_10_s_at_any_ping_rate(int pingsPerSecond, int lastInMs)
    {
        var interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / pingsPerSecond);
        var start = _time.GetUtcNow();
        Rtt(5);
        for (int i = 1; i < 10 * pingsPerSecond; i++)
        {
            _time.Advance(interval);
            Rtt(20);
        }

        _time.Advance(start + TimeSpan.FromMilliseconds(lastInMs) - _time.GetUtcNow());
        Assert.Equal(TimeSpan.FromMilliseconds(5), _guard.RttBaseline);
        Assert.True(_guard.IsDegraded); // 20 ms against the 5 ms baseline
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(20), _guard.RttBaseline);
        Rtt(20);
        Assert.False(_guard.IsDegraded);
    }

    [Fact]
    public void Paused_while_the_rtt_stays_high_waits_for_a_sample()
    {
        Degrade();
        while (!_guard.Level.Paused)
        {
            Assert.True(_guard.Update());
            _time.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Null(_guard.NextUpdateIn);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(_guard.Update());
        Assert.True(_guard.Level.Paused);
    }

    [Theory]
    [InlineData(90, new[] { 45, 30 })]
    [InlineData(60, new[] { 30 })]
    [InlineData(30, new int[0])]
    [InlineData(24, new int[0])]
    public void Fps_halves_down_to_30_and_never_below(int fps, int[] halvings)
    {
        _guard.Start(Plan with { Fps = fps, TargetBitrateKbps = 5000 });
        Degrade();
        foreach (int expected in halvings)
        {
            Assert.True(_guard.Update());
            Assert.Equal(new MirrorGuardLevel(5000, expected, false), _guard.Level);
            _time.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.True(_guard.Update());
        Assert.Equal(new MirrorGuardLevel(5000, halvings.Length > 0 ? halvings[^1] : fps, true), _guard.Level);
    }

    [Fact]
    public void Retarget_keeps_the_guard_position()
    {
        Degrade();
        Assert.True(_guard.Update()); // 14000 = 70 %
        _guard.Retarget(Plan with { TargetBitrateKbps = 10000, FloorBitrateKbps = 3000 });
        Assert.Equal(new MirrorGuardLevel(7000, 120, false), _guard.Level);

        _time.Advance(TimeSpan.FromMilliseconds(500));
        while (_guard.Level.Fps == 120)
        {
            Assert.True(_guard.Update());
            _time.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(new MirrorGuardLevel(3000, 60, false), _guard.Level);
        _guard.Retarget(Plan with { Fps = 60 }); // 30 % of 20000; the halving still applies
        Assert.Equal(new MirrorGuardLevel(6000, 30, false), _guard.Level);
        _guard.Retarget(Plan with { Fps = 30 }); // a halving that no longer does anything is dropped
        Assert.Equal(new MirrorGuardLevel(6000, 30, false), _guard.Level);
        _guard.Retarget(Plan with { Fps = 240 });
        Assert.Equal(new MirrorGuardLevel(6000, 240, false), _guard.Level);
    }

    [Fact]
    public void Start_resets_the_level_and_stop_ends_it()
    {
        Degrade();
        Assert.True(_guard.Update());
        _guard.Start(Plan);
        Assert.Equal(new MirrorGuardLevel(20000, 120, false), _guard.Level);

        _guard.Stop();
        Assert.Equal(default, _guard.Level);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_guard.Update());
        Assert.Null(_guard.NextUpdateIn);

        var fresh = new MirrorRateController(_time);
        fresh.Retarget(Plan); // starts when not running
        Assert.Equal(new MirrorGuardLevel(20000, 120, false), fresh.Level);
        Assert.Throws<ArgumentOutOfRangeException>(() => fresh.Start(Plan with { TargetBitrateKbps = 0 }));
        Assert.Throws<ArgumentNullException>(() => new MirrorRateController(null!));
    }

    private void Degrade() => Rtt(10, 30, 30);

    private void Rtt(params int[] ms)
    {
        foreach (int m in ms)
        {
            _guard.OnRttSample(TimeSpan.FromMilliseconds(m));
        }
    }
}
