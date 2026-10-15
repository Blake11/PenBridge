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
        Assert.True(TabletEventMath.Tilt(3000) > 0);
        Assert.True(TabletEventMath.Tilt(-3000) < 0);
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
        Assert.True(sink != 0);
    }
}
