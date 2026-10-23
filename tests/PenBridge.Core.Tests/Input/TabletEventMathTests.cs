using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

[Collection(AllocationGroup.Name)]
public class TabletEventMathTests
{
    private const long MaxLagNs = 100_000_000;

    [Theory]
    [InlineData((ushort)0, 0.0)]
    [InlineData((ushort)65535, 1.0)]
    [InlineData((ushort)32768, 32768 / 65535.0)]
    [InlineData((ushort)1, 1 / 65535.0)]
    public void Pressure_maps_full_range_to_unit_interval(ushort wire, double expected)
    {
        Assert.Equal(expected, TabletEventMath.Pressure(wire), 12);
    }

    [Theory]
    [InlineData((short)0, 0.0)]
    [InlineData((short)6000, 1.0)]
    [InlineData((short)-6000, -1.0)]
    [InlineData((short)3000, 0.5)]
    [InlineData((short)-1, -1 / 6000.0)]
    [InlineData((short)5999, 5999 / 6000.0)]
    [InlineData((short)6001, 1.0)]
    [InlineData((short)9000, 1.0)]
    [InlineData((short)-9000, -1.0)]
    [InlineData(short.MaxValue, 1.0)]
    [InlineData(short.MinValue, -1.0)]
    public void Tilt_scales_60_degrees_to_one_and_clamps(short wire, double expected)
    {
        Assert.Equal(expected, TabletEventMath.Tilt(wire), 12);
    }

    [Fact]
    public void Tilt_keeps_the_sign_convention()
    {
        // +x right, +y toward the user: same as CGEvent, no flip.
        Assert.InRange(TabletEventMath.Tilt(3000), double.Epsilon, double.PositiveInfinity);
        Assert.InRange(TabletEventMath.Tilt(-3000), double.NegativeInfinity, -double.Epsilon);
    }

    [Theory]
    [InlineData((ushort)0, 0.0)]
    [InlineData((ushort)35999, 359.99)]
    [InlineData((ushort)9000, 90.0)]
    [InlineData((ushort)1, 0.01)]
    public void Rotation_converts_centi_degrees_to_degrees(ushort wire, double expected)
    {
        Assert.Equal(expected, TabletEventMath.RotationDegrees(wire), 9);
    }

    [Theory]
    [InlineData(123_456_789L, 1_000_000_000L, 123_456_789L)]
    [InlineData(10_000_000L, 10_000_000L, 1_000_000_000L)]
    [InlineData(3L, 24_000_000L, 125L)]
    [InlineData(24L, 24_000_000L, 1_000L)] // 1e9/24e6 is inexact in floating point; must not truncate to 999
    [InlineData(7_693_175_713_574L, 24_000_000L, 320_548_988_065_583L)] // ~3.7 days of Apple-silicon mach ticks
    public void Ticks_convert_to_nanoseconds(long ticks, long frequency, long expected)
    {
        Assert.Equal(expected, TabletEventMath.TicksToNanoseconds(ticks, frequency));
    }

    [Fact]
    public void First_sample_anchors_to_now()
    {
        var anchor = default(HostClockAnchor);
        Assert.Equal(5_000_000_000, TabletEventMath.MapTimestamp(ref anchor, 1_000, 5_000_000_000, MaxLagNs));
        Assert.True(anchor.IsSet);
        Assert.Equal(5_000_000_000 - 1_000_000, anchor.OffsetNs);
    }

    [Fact]
    public void Spacing_between_samples_is_preserved()
    {
        var anchor = default(HostClockAnchor);
        long now = 5_000_000_000;
        TabletEventMath.MapTimestamp(ref anchor, 10_000, now, MaxLagNs);

        // A batch arriving 20 ms later: history samples 4 ms apart, the last at +20 ms.
        now += 20_000_000;
        Assert.Equal(now - 8_000_000, TabletEventMath.MapTimestamp(ref anchor, 22_000, now, MaxLagNs));
        Assert.Equal(now - 4_000_000, TabletEventMath.MapTimestamp(ref anchor, 26_000, now, MaxLagNs));
        Assert.Equal(now, TabletEventMath.MapTimestamp(ref anchor, 30_000, now, MaxLagNs));
    }

    [Fact]
    public void A_faster_arrival_moves_the_anchor_so_events_never_land_in_the_future()
    {
        var anchor = default(HostClockAnchor);
        long now = 5_000_000_000;
        TabletEventMath.MapTimestamp(ref anchor, 0, now, MaxLagNs); // arrived with 30 ms latency (unknown to us)

        // Next sample 10 ms of phone time later arrives only 2 ms later (less latency): it would map 8 ms ahead.
        now += 2_000_000;
        Assert.Equal(now, TabletEventMath.MapTimestamp(ref anchor, 10_000, now, MaxLagNs));
        Assert.Equal(now - 10_000_000, anchor.OffsetNs);
    }

    [Fact]
    public void A_stall_or_a_new_session_re_anchors_after_max_lag()
    {
        var anchor = default(HostClockAnchor);
        long now = 5_000_000_000;
        TabletEventMath.MapTimestamp(ref anchor, 50_000_000, now, MaxLagNs);

        // New session: its clock restarts near 0, which would map 50 s in the past.
        now += 1_000_000;
        Assert.Equal(now, TabletEventMath.MapTimestamp(ref anchor, 200, now, MaxLagNs));

        // Exactly max lag behind is still accepted.
        long accepted = TabletEventMath.MapTimestamp(ref anchor, 200, now + MaxLagNs, MaxLagNs);
        Assert.Equal(now, accepted);
    }

    [Fact]
    public void Offset_math_works_far_into_an_unwrapped_timeline()
    {
        // Upstream unwraps the u32 µs clock to 64 bits; past 2^32 µs (~71.6 min) the offset math must stay exact.
        var anchor = default(HostClockAnchor);
        long beforeWrap = uint.MaxValue - 1_000L;
        long now = 9_000_000_000;
        TabletEventMath.MapTimestamp(ref anchor, beforeWrap, now, MaxLagNs);

        // A batch straddling 2^32 µs, arriving 3 ms later: spacing is kept across the old wrap point.
        now += 3_000_000;
        Assert.Equal(now - 1_000_000, TabletEventMath.MapTimestamp(ref anchor, beforeWrap + 2_000, now, MaxLagNs));
        Assert.Equal(now, TabletEventMath.MapTimestamp(ref anchor, beforeWrap + 3_000, now, MaxLagNs));
    }

    [Fact]
    public void Mapped_timestamps_never_go_backwards()
    {
        var anchor = default(HostClockAnchor);
        long now = 5_000_000_000;
        long a = TabletEventMath.MapTimestamp(ref anchor, 10_000, now, MaxLagNs);
        long b = TabletEventMath.MapTimestamp(ref anchor, 9_000, now, MaxLagNs); // older sample
        Assert.Equal(a, b);
        Assert.Equal(a, TabletEventMath.Now(ref anchor, now - 5));
        Assert.Equal(now + 7, TabletEventMath.Now(ref anchor, now + 7));
    }

    [Fact]
    public void Click_count_increments_within_interval_and_slop_only()
    {
        var s = default(ClickState);
        const long Interval = 500_000_000;
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 1_000_000_000, new DisplayPoint(100, 100), Interval, 6));
        Assert.Equal(2, TabletEventMath.NextClickCount(ref s, 1_400_000_000, new DisplayPoint(103, 104), Interval, 6));
        Assert.Equal(3, TabletEventMath.NextClickCount(ref s, 1_900_000_000, new DisplayPoint(103, 104), Interval, 6));
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 2_400_000_001, new DisplayPoint(103, 104), Interval, 6)); // too late
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 2_500_000_000, new DisplayPoint(110, 104), Interval, 6)); // moved 7 pt
    }

    [Fact]
    public void Math_is_allocation_free()
    {
        var anchor = default(HostClockAnchor);
        var clicks = default(ClickState);
        double sink = 0;
        long ran = 0;
        Alloc.AssertZero(2_000, i =>
        {
            sink += TabletEventMath.Pressure((ushort)i) + TabletEventMath.Tilt((short)(i - 9000))
                + TabletEventMath.RotationDegrees((ushort)(i % 36000));
            sink += TabletEventMath.MapTimestamp(ref anchor, i * 4_000L, 1_000_000_000L + (i * 4_000_000L), MaxLagNs);
            sink += TabletEventMath.NextClickCount(ref clicks, i * 1_000_000L, new DisplayPoint(i % 3, 0), 500_000_000, 6);
            sink += TabletEventMath.TicksToNanoseconds(i, 24_000_000);
            ran++;
        }, () => ran);
        Assert.NotEqual(0, sink);
    }

    [Fact]
    public void Tilt_is_exact_at_the_full_scale_and_one_step_inside_it()
    {
        Assert.Equal(1, TabletEventMath.Tilt(6000));
        Assert.Equal(-1, TabletEventMath.Tilt(-6000));
        Assert.Equal(1, TabletEventMath.Tilt(6001));
        Assert.Equal(-1, TabletEventMath.Tilt(-6001));
        Assert.Equal(5999 / 6000.0, TabletEventMath.Tilt(5999));
        Assert.Equal(-5999 / 6000.0, TabletEventMath.Tilt(-5999));
        Assert.Equal(1 / 6000.0, TabletEventMath.Tilt(1));
    }

    [Fact]
    public void Ticks_to_nanoseconds_rejects_a_zero_or_negative_frequency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TabletEventMath.TicksToNanoseconds(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TabletEventMath.TicksToNanoseconds(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TabletEventMath.TicksToNanoseconds(1, long.MinValue));
    }

    [Theory]
    [InlineData(1L, 1L, 1_000_000_000L)]
    [InlineData(5L, 1_000L, 5_000_000L)]
    [InlineData(1_000_000_000L, 1_000_000_001L, 999_999_999L)] // just off the 1 GHz identity, rounded down
    [InlineData(1_000_000_000L, 999_999_999L, 1_000_000_001L)]
    [InlineData(-24L, 24_000_000L, -1_000L)]
    [InlineData(long.MaxValue, 1_000_000_000L, long.MaxValue)]
    [InlineData(0L, 24_000_000L, 0L)]
    public void Ticks_to_nanoseconds_in_more_cases(long ticks, long frequency, long expected)
    {
        Assert.Equal(expected, TabletEventMath.TicksToNanoseconds(ticks, frequency));
    }

    [Fact]
    public void A_sample_exactly_now_or_exactly_max_lag_behind_keeps_the_anchor()
    {
        var anchor = default(HostClockAnchor);
        long now = 5_000_000_000;
        TabletEventMath.MapTimestamp(ref anchor, 1_000, now, MaxLagNs);
        long offset = anchor.OffsetNs;

        // Same sample time again at the same now: it maps exactly to now, which is not in the future.
        Assert.Equal(now, TabletEventMath.MapTimestamp(ref anchor, 1_000, now, MaxLagNs));
        Assert.Equal(offset, anchor.OffsetNs);

        // Exactly max lag behind: kept (offset unchanged, the result is the older time, but never below the last one).
        long later = now + MaxLagNs;
        Assert.Equal(now, TabletEventMath.MapTimestamp(ref anchor, 1_000, later, MaxLagNs));
        Assert.Equal(offset, anchor.OffsetNs);

        // One ns beyond max lag: re-anchored to now.
        Assert.Equal(later + 1, TabletEventMath.MapTimestamp(ref anchor, 1_000, later + 1, MaxLagNs));
        Assert.Equal(offset + MaxLagNs + 1, anchor.OffsetNs);
    }

    [Fact]
    public void One_ns_in_the_future_re_anchors()
    {
        var anchor = default(HostClockAnchor);
        TabletEventMath.MapTimestamp(ref anchor, 0, 1_000_000_000, MaxLagNs);
        // 1 us later in phone time arriving 999 ns later: mapped = now + 1.
        Assert.Equal(1_000_000_999, TabletEventMath.MapTimestamp(ref anchor, 1, 1_000_000_999, MaxLagNs));
        Assert.Equal(1_000_000_999 - 1_000, anchor.OffsetNs);
        // Exactly on time: no re-anchor.
        long offset = anchor.OffsetNs;
        Assert.Equal(1_000_001_999, TabletEventMath.MapTimestamp(ref anchor, 2, 1_000_001_999, MaxLagNs));
        Assert.Equal(offset, anchor.OffsetNs);
    }

    [Fact]
    public void Map_timestamp_stores_the_last_value_and_the_set_flag()
    {
        var anchor = default(HostClockAnchor);
        Assert.False(anchor.IsSet);
        long t = TabletEventMath.MapTimestamp(ref anchor, 0, 7_000, MaxLagNs);
        Assert.Equal(7_000, t);
        Assert.Equal(7_000, anchor.LastNs);
        Assert.Equal(7_000, anchor.OffsetNs);
        Assert.True(anchor.IsSet);
        // A negative-offset case: sample clock far ahead of the host clock.
        var far = default(HostClockAnchor);
        Assert.Equal(1_000, TabletEventMath.MapTimestamp(ref far, 9_000_000_000, 1_000, MaxLagNs));
        Assert.Equal(1_000 - 9_000_000_000_000, far.OffsetNs);
    }

    [Fact]
    public void Now_never_goes_before_the_last_mapped_time_and_stores_the_result()
    {
        var anchor = default(HostClockAnchor);
        TabletEventMath.MapTimestamp(ref anchor, 0, 1_000, MaxLagNs);
        Assert.Equal(1_000, TabletEventMath.Now(ref anchor, 1_000)); // equal
        Assert.Equal(1_000, TabletEventMath.Now(ref anchor, 999));
        Assert.Equal(1_001, TabletEventMath.Now(ref anchor, 1_001));
        Assert.Equal(1_001, anchor.LastNs);
        Assert.Equal(1_001, TabletEventMath.Now(ref anchor, 5)); // the stored value is the new floor
        Assert.Equal(1_001, TabletEventMath.MapTimestamp(ref anchor, 0, 1_001, MaxLagNs));
    }

    [Fact]
    public void Finger_fallback_never_double_clicks_by_slop_and_the_pen_uses_the_host_slop()
    {
        Assert.Equal(0, TabletEventMath.ClickSlopFor(PenTool.FingerFallback, 6));
        Assert.Equal(6, TabletEventMath.ClickSlopFor(PenTool.Pen, 6));
        Assert.Equal(6, TabletEventMath.ClickSlopFor(PenTool.Eraser, 6));
        Assert.Equal(4.5, TabletEventMath.ClickSlopFor(PenTool.None, 4.5));
    }

    [Fact]
    public void Click_count_repeats_on_a_single_axis_offset_up_to_the_slop()
    {
        const long Interval = 500;
        var s = default(ClickState);
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 1_000, new DisplayPoint(100, 100), Interval, 3));
        Assert.Equal(2, TabletEventMath.NextClickCount(ref s, 1_100, new DisplayPoint(100, 103), Interval, 3)); // y only, exactly the slop
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 1_200, new DisplayPoint(100, 106.5), Interval, 3)); // y only, past it
        Assert.Equal(2, TabletEventMath.NextClickCount(ref s, 1_300, new DisplayPoint(103, 106.5), Interval, 3)); // x only, exactly the slop
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 1_400, new DisplayPoint(106.5, 106.5), Interval, 3)); // x only, past it
        Assert.Equal(2, TabletEventMath.NextClickCount(ref s, 1_500, new DisplayPoint(109.5, 110.5), Interval, 5)); // 3-4-5: on the circle
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 1_600, new DisplayPoint(112.5, 114.51), Interval, 5));
    }

    [Fact]
    public void Click_count_interval_is_inclusive_and_a_press_always_updates_the_state()
    {
        const long Interval = 500;
        var s = default(ClickState);
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 100, new DisplayPoint(0, 0), Interval, 6)); // fresh state at the origin
        Assert.Equal(1, s.Count);
        Assert.Equal(100, s.TimeNs);
        Assert.Equal(2, TabletEventMath.NextClickCount(ref s, 600, new DisplayPoint(1, 2), Interval, 6)); // exactly the interval
        Assert.Equal(600, s.TimeNs);
        Assert.Equal(new DisplayPoint(1, 2), s.Position);
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 1_101, new DisplayPoint(1, 2), Interval, 6)); // one ns late
        Assert.Equal(1_101, s.TimeNs);
        Assert.Equal(2, TabletEventMath.NextClickCount(ref s, 1_101, new DisplayPoint(1, 2), Interval, 0)); // slop 0: the same point repeats
        Assert.Equal(1, TabletEventMath.NextClickCount(ref s, 1_102, new DisplayPoint(1, 2.001), Interval, 0));
    }

    [Fact]
    public void Click_count_is_measured_from_the_last_press_not_the_first()
    {
        const long Interval = 500;
        var s = default(ClickState);
        TabletEventMath.NextClickCount(ref s, 0, new DisplayPoint(0, 0), Interval, 4);
        TabletEventMath.NextClickCount(ref s, 400, new DisplayPoint(3, 0), Interval, 4);
        // 800 ns after the first, 400 after the second; 6 from the first, 3 from the second.
        Assert.Equal(3, TabletEventMath.NextClickCount(ref s, 800, new DisplayPoint(6, 0), Interval, 4));
    }
}
