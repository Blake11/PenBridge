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
    public void The_ladder_steps_bitrate_then_fps_once_per_500_ms_then_pauses_after_2_s_at_the_bottom()
    {
        Degrade();
        MirrorGuardLevel[] expected =
        [
            new(14000, 120, false),
            new(9800, 120, false),
            new(6860, 120, false),
            new(5000, 120, false), // down to the floor
            new(5000, 60, false),  // fps halves
            new(5000, 30, false),  // the bottom
        ];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(_guard.Update(), $"step {i}");
            Assert.Equal(expected[i], _guard.Level);
            Assert.False(_guard.Update()); // one step per interval
            Assert.Equal(i == expected.Length - 1 ? MirrorRateController.SustainedBeforePause : TimeSpan.FromMilliseconds(500), _guard.NextUpdateIn);
            if (i < expected.Length - 1)
            {
                _time.Advance(TimeSpan.FromMilliseconds(499));
                Assert.False(_guard.Update());
                _time.Advance(TimeSpan.FromMilliseconds(1));
            }
        }

        // The bottom gets 2 s of unbroken degradation before the video pauses.
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(_guard.Update());
        Assert.Equal(new MirrorGuardLevel(5000, 30, false), _guard.Level);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_guard.Update());
        Assert.Equal(new MirrorGuardLevel(5000, 30, true), _guard.Level);
        Assert.Null(_guard.NextUpdateIn);
        Assert.False(_guard.Update()); // paused: nothing further
    }

    [Fact]
    public void A_single_blip_at_the_bottom_does_not_pause()
    {
        // The device-round case: a small target whose plan already sits at the floor and 30 fps.
        _guard.Start(Plan with { Fps = 30, TargetBitrateKbps = 2000, FloorBitrateKbps = 2000 });
        _time.Advance(TimeSpan.FromSeconds(5));
        _guard.OnVideoLoss(10, 100);
        Assert.True(_guard.IsDegraded);
        Assert.False(_guard.Update()); // nothing left but the pause, and it isn't due
        Assert.Equal(new MirrorGuardLevel(2000, 30, false), _guard.Level);
        Assert.Equal(MirrorRateController.SustainedBeforePause, _guard.NextUpdateIn); // when the pause would be due

        _time.Advance(MirrorRateController.VideoLossSignal);
        Assert.False(_guard.IsDegraded);
        Assert.False(_guard.Update());
        Assert.Null(_guard.NextUpdateIn); // at the plan and healthy
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_guard.Update());
        Assert.Equal(new MirrorGuardLevel(2000, 30, false), _guard.Level);
    }

    [Fact]
    public void Blips_with_healthy_breaks_never_pause()
    {
        _guard.Start(Plan with { Fps = 30, TargetBitrateKbps = 2000, FloorBitrateKbps = 2000 });
        for (int i = 0; i < 20; i++)
        {
            _guard.OnVideoLoss(10, 100); // 1 s degraded, then 200 ms healthy
            _time.Advance(TimeSpan.FromMilliseconds(600));
            Assert.False(_guard.Update());
            _time.Advance(TimeSpan.FromMilliseconds(600));
            Assert.False(_guard.Update());
        }

        Assert.False(_guard.Level.Paused);
    }

    [Fact]
    public void Two_seconds_of_unbroken_degradation_at_the_bottom_pause()
    {
        _guard.Start(Plan with { Fps = 30, TargetBitrateKbps = 2000, FloorBitrateKbps = 2000 });
        _time.Advance(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 5; i++)
        {
            _guard.OnVideoLoss(10, 100); // each signal overlaps the next: no healthy moment
            Assert.False(_guard.Update());
            _time.Advance(TimeSpan.FromMilliseconds(400));
        }

        // 2000 ms after the first signal.
        _guard.OnVideoLoss(10, 100);
        Assert.Equal(TimeSpan.Zero, _guard.NextUpdateIn);
        Assert.True(_guard.Update());
        Assert.Equal(new MirrorGuardLevel(2000, 30, true), _guard.Level);
    }

    [Fact]
    public void A_loss_signal_lasts_two_report_intervals()
    {
        Assert.Equal(2 * MirrorRateController.ReportInterval, MirrorRateController.VideoLossSignal);
        Assert.Equal(MirrorRateController.ReportInterval, PenBridge.Core.Net.ClientMirror.ReportInterval);
        _guard.OnVideoLoss(10, 100);
        _time.Advance(MirrorRateController.VideoLossSignal - TimeSpan.FromMilliseconds(1));
        Assert.True(_guard.IsDegraded);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(_guard.IsDegraded);
    }

    [Fact]
    public void Sustained_loss_reported_with_jitter_reaches_the_pause()
    {
        // Reports about every 500 ms, some late: each loss signal still overlaps the next, so the degradation never
        // breaks; the ladder runs down (6 steps, 500 ms apart) and the bottom's 2 s pass.
        int[] gapsMs = [450, 650, 520, 610, 480, 640, 500, 560, 470, 630, 540, 600];
        var reportAt = new List<int> { 0 };
        foreach (int gap in gapsMs)
        {
            reportAt.Add(reportAt[^1] + gap);
        }

        int next = 0;
        bool paused = false;
        for (int ms = 0; ms <= reportAt[^1] && !paused; ms += 10)
        {
            if (next < reportAt.Count && ms >= reportAt[next])
            {
                _guard.OnVideoLoss(10, 100);
                next++;
            }

            Assert.True(_guard.IsDegraded, $"degraded at {ms} ms");
            _guard.Update();
            paused = _guard.Level.Paused;
            _time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(paused);
        Assert.Equal(new MirrorGuardLevel(5000, 30, true), _guard.Level);
    }

    [Fact]
    public void Recovery_from_a_pause_starts_at_the_floor_and_lowest_fps()
    {
        PauseAtTheBottom();
        // A retarget during the pause keeps the bitrate's fraction (25 % of 30000) and the halvings (240 → 60).
        _guard.Retarget(Plan with { Fps = 240, TargetBitrateKbps = 30000, FloorBitrateKbps = 3000 });
        Assert.Equal(new MirrorGuardLevel(7500, 60, true), _guard.Level);

        Rtt(10); // healthy again
        _time.Advance(MirrorRateController.HealthyBeforeRecovery);
        Assert.True(_guard.Update());
        Assert.Equal(new MirrorGuardLevel(3000, 30, false), _guard.Level); // the new plan's bottom
        _time.Advance(MirrorRateController.RecoveryInterval);
        Assert.True(_guard.Update());
        Assert.Equal(new MirrorGuardLevel(3000, 60, false), _guard.Level); // then climbs as before
    }

    [Fact]
    public void After_a_resume_the_bottom_gets_2_s_again()
    {
        PauseAtTheBottom();
        Rtt(10);
        _time.Advance(MirrorRateController.HealthyBeforeRecovery);
        Assert.True(_guard.Update());
        Assert.False(_guard.Level.Paused);

        Degrade();
        Assert.False(_guard.Update());
        Assert.Equal(MirrorRateController.SustainedBeforePause, _guard.NextUpdateIn);
        _time.Advance(MirrorRateController.SustainedBeforePause);
        Assert.True(_guard.Update());
        Assert.True(_guard.Level.Paused);
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
    public void Recorded_reports_set_the_baseline_without_signalling()
    {
        _guard.OnMirrorReport(Report(frames: 10, lost: 0), currentEpoch: 1, fragmentsSentInEpoch: 100);
        _guard.RecordMirrorReport(Report(frames: 11, lost: 50), currentEpoch: 1, fragmentsSentInEpoch: 110); // the pause cut a frame
        Assert.False(_guard.IsDegraded);
        _guard.RecordMirrorReport(Report(frames: 11, lost: 50), currentEpoch: 0, fragmentsSentInEpoch: 110); // another epoch: ignored
        _guard.OnMirrorReport(Report(frames: 20, lost: 51), currentEpoch: 1, fragmentsSentInEpoch: 210);
        Assert.False(_guard.IsDegraded); // 1 of 100 since the recorded report
    }

    [Fact]
    public void After_a_rebaseline_reports_are_quiet_until_the_resume_keyframe_shows()
    {
        _guard.OnMirrorReport(Report(frames: 10, lost: 0), currentEpoch: 1, fragmentsSentInEpoch: 100);
        _guard.RecordMirrorReport(Report(frames: 11, lost: 20), currentEpoch: 1, fragmentsSentInEpoch: 110); // paused
        _guard.RebaselineReports(epoch: 1); // resume

        _time.Advance(TimeSpan.FromMilliseconds(100));
        _guard.OnMirrorReport(Report(frames: 11, lost: 20), currentEpoch: 1, fragmentsSentInEpoch: 120);
        Assert.False(_guard.IsDegraded); // before the keyframe: nothing new
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _guard.OnMirrorReport(Report(frames: 12, lost: 60), currentEpoch: 1, fragmentsSentInEpoch: 200);
        Assert.False(_guard.IsDegraded); // the keyframe, with the frames the pause dropped counted as lost: quiet
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _guard.OnMirrorReport(Report(frames: 20, lost: 70), currentEpoch: 1, fragmentsSentInEpoch: 300);
        Assert.True(_guard.IsDegraded); // real loss after it counts again (10 of 100)
    }

    [Fact]
    public void A_rebaseline_stays_quiet_for_at_most_three_reports()
    {
        _guard.OnMirrorReport(Report(frames: 10, lost: 0), currentEpoch: 1, fragmentsSentInEpoch: 100);
        _guard.RebaselineReports(epoch: 1);
        for (int i = 1; i <= MirrorRateController.ResumeQuietReports; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1)); // late reports: counted, not timed
            _guard.OnMirrorReport(Report(frames: 10, lost: (uint)(10 * i)), currentEpoch: 1, fragmentsSentInEpoch: (ulong)(100 + (100 * i)));
            Assert.False(_guard.IsDegraded); // the resume keyframe never came: still quiet
        }

        _guard.OnMirrorReport(Report(frames: 10, lost: 40), currentEpoch: 1, fragmentsSentInEpoch: 500);
        Assert.True(_guard.IsDegraded); // the quiet reports are used up

        _guard.Start(Plan);
        _guard.RebaselineReports(epoch: 1);
        _guard.Stop();
        _guard.Start(Plan); // a new stream drops the quiet reports
        _time.Advance(TimeSpan.FromSeconds(5));
        _guard.OnMirrorReport(Report(frames: 1, lost: 0, epoch: 2), currentEpoch: 2, fragmentsSentInEpoch: 100);
        _guard.OnMirrorReport(Report(frames: 1, lost: 10, epoch: 2), currentEpoch: 2, fragmentsSentInEpoch: 200);
        Assert.True(_guard.IsDegraded);
    }

    [Fact]
    public void A_new_epoch_ends_the_quiet_reports()
    {
        _guard.OnMirrorReport(Report(frames: 10, lost: 0), currentEpoch: 1, fragmentsSentInEpoch: 100);
        _guard.RebaselineReports(epoch: 1);

        // A MirrorStart right after the resume: the new epoch's first reports count from its MirrorConfig at once.
        _guard.OnMirrorReport(Report(frames: 1, lost: 0, epoch: 2), currentEpoch: 2, fragmentsSentInEpoch: 100);
        _guard.OnMirrorReport(Report(frames: 1, lost: 10, epoch: 2), currentEpoch: 2, fragmentsSentInEpoch: 200);
        Assert.True(_guard.IsDegraded);
    }

    private static MirrorReportMessage Report(uint frames, uint lost, byte epoch = 1) => new(epoch, frames, 0, lost, 0, 0);

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
        PauseAtTheBottom();
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

        AdvanceToSustainedPause();
        Assert.Equal(new MirrorGuardLevel(5000, halvings.Length > 0 ? halvings[^1] : fps, true), _guard.Level);
    }

    [Theory]
    [InlineData(120, 120, new[] { 60, 30 })]
    [InlineData(90, 90, new[] { 45, 30 })]
    [InlineData(90, 120, new[] { 40, 30 })]  // 45 snaps down to a divisor of 120
    [InlineData(100, 100, new[] { 50, 30 })] // 25 would be below 30
    public void Fps_steps_snap_to_divisors_of_the_phone_refresh(int fps, int phoneMaxFps, int[] steps)
    {
        _guard.Start(Plan with { Fps = fps, TargetBitrateKbps = 5000, PhoneMaxFps = phoneMaxFps });
        Degrade();
        foreach (int expected in steps)
        {
            Assert.True(_guard.Update());
            Assert.Equal(new MirrorGuardLevel(5000, expected, false), _guard.Level);
            _time.Advance(TimeSpan.FromMilliseconds(500));
        }

        AdvanceToSustainedPause();
        Assert.True(_guard.Level.Paused);
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

    /// <summary>At the bottom of the ladder and still degraded: no step until the pause is due, then the pause.</summary>
    private void AdvanceToSustainedPause()
    {
        Assert.False(_guard.Update());
        TimeSpan? due = _guard.NextUpdateIn;
        Assert.NotNull(due);
        Assert.InRange(due.Value, TimeSpan.FromTicks(1), MirrorRateController.SustainedBeforePause);
        _time.Advance(due.Value - TimeSpan.FromMilliseconds(1));
        Assert.False(_guard.Update());
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_guard.Update());
    }

    /// <summary>Degrades from the full plan (120 fps, 20000 → floor 5000) until the video pauses.</summary>
    private void PauseAtTheBottom()
    {
        Degrade();
        for (int i = 0; i < 100 && !_guard.Level.Paused; i++)
        {
            _guard.Update();
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(new MirrorGuardLevel(5000, 30, true), _guard.Level);
    }

    private void Rtt(params int[] ms)
    {
        foreach (int m in ms)
        {
            _guard.OnRttSample(TimeSpan.FromMilliseconds(m));
        }
    }
}
