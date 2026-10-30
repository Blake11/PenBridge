using PenBridge.Core.Input;
using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Mirror;

/// <summary>docs/specs/screen-mirroring.md "Codec choice", "Size", "Quality presets".</summary>
public class MirrorStreamPlannerTests
{
    private const MirrorCodecs Both = MirrorCodecs.H264 | MirrorCodecs.Hevc;

    [Theory]
    [InlineData(Both, Both, MirrorCodec.Hevc)]
    [InlineData(Both, MirrorCodecs.H264, MirrorCodec.H264)]
    [InlineData(MirrorCodecs.H264, Both, MirrorCodec.H264)]
    [InlineData(MirrorCodecs.Hevc, Both, MirrorCodec.Hevc)]
    [InlineData((MirrorCodecs)0xFF, MirrorCodecs.H264, MirrorCodec.H264)] // unknown bits ignored
    public void Codec_prefers_hevc_then_h264(MirrorCodecs phone, MirrorCodecs mac, MirrorCodec expected) =>
        Assert.Equal(expected, MirrorStreamPlanner.ChooseCodec(phone, mac));

    [Theory]
    [InlineData(MirrorCodecs.Hevc, MirrorCodecs.H264)]
    [InlineData(MirrorCodecs.None, Both)]
    [InlineData(Both, MirrorCodecs.None)]
    [InlineData((MirrorCodecs)0x80, (MirrorCodecs)0x80)]
    public void No_common_codec(MirrorCodecs phone, MirrorCodecs mac) => Assert.Null(MirrorStreamPlanner.ChooseCodec(phone, mac));

    [Theory]
    [InlineData(2228, 1440, 3456, 2234, 8192, 8192, 2228, 1440)] // fits: unchanged
    [InlineData(3000, 1500, 1920, 960, 8192, 8192, 1920, 960)]   // capped by the source
    [InlineData(3000, 1500, 8000, 4000, 1920, 1080, 1920, 960)]  // capped by the decoder width
    [InlineData(3000, 1500, 8000, 4000, 4096, 1200, 2400, 1200)] // capped by the decoder height
    [InlineData(4000, 2000, 8000, 4000, 8192, 8192, 3840, 1920)] // 3840 long side
    [InlineData(4000, 2400, 8000, 4800, 8192, 8192, 3600, 2160)] // 2160 short side
    [InlineData(1500, 4000, 8000, 8000, 8192, 8192, 1440, 3840)] // portrait: the cap turns with it
    [InlineData(2400, 4000, 8000, 8000, 8192, 8192, 2160, 3600)]
    [InlineData(1001, 601, 4000, 4000, 4000, 4000, 1000, 600)]   // rounded down to even
    [InlineData(3001, 1001, 1999, 4000, 4000, 4000, 1998, 666)]  // exact integer scaling, then even
    [InlineData(1, 1, 100, 100, 100, 100, 2, 2)]                 // at least 2×2
    [InlineData(1440, 3088, 3456, 2234, 8192, 8192, 1040, 2234)] // portrait phone, landscape source cap on height
    public void Size_keeps_the_aspect_ratio_under_every_cap(int aw, int ah, int sw, int sh, int maxW, int maxH, int w, int h)
    {
        Assert.Equal((w, h), MirrorStreamPlanner.StreamSize(aw, ah, new MirrorSource(sw, sh, 60), maxW, maxH));
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }

    [Fact]
    public void Size_rejects_empty_inputs()
    {
        var source = new MirrorSource(100, 100, 60);
        Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.StreamSize(0, 1, source, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.StreamSize(1, 0, source, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.StreamSize(1, 1, source with { WidthPx = 0 }, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.StreamSize(1, 1, source with { HeightPx = 0 }, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.StreamSize(1, 1, source, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.StreamSize(1, 1, source, 1, 0));
    }

    [Theory]
    [InlineData(60, 120, 0, 0, 60)]    // Auto: the source's refresh
    [InlineData(120, 120, 0, 0, 120)]
    [InlineData(144, 120, 0, 0, 120)]  // Auto: the phone's refresh
    [InlineData(120, 120, 60, 0, 60)]  // a limit
    [InlineData(120, 120, 30, 0, 30)]
    [InlineData(60, 120, 120, 0, 60)]  // a limit above both changes nothing
    [InlineData(120, 120, 0, 90, 90)]  // the encoder's capability
    [InlineData(120, 120, 60, 50, 50)]
    [InlineData(0, 0, 0, 0, 1)]        // never below 1
    [InlineData(300, 300, 0, 0, 255)]  // fits the u8 field
    public void Fps_follows_the_refresh_rates_under_the_limits(int source, int phone, int limit, int encoder, int expected) =>
        Assert.Equal(expected, MirrorStreamPlanner.Fps(source, phone, limit, encoder));

    [Theory]
    [InlineData(MirrorQuality.Balanced, MirrorCodec.Hevc, 2500, 1000, 60, 12000, 3000)] // the reference point
    [InlineData(MirrorQuality.Balanced, MirrorCodec.Hevc, 2500, 1000, 30, 7917, 2000)]  // × 0.5^0.6, floor clamped to 2 Mbps
    [InlineData(MirrorQuality.Balanced, MirrorCodec.Hevc, 2500, 1000, 120, 18189, 4547)] // × 2^0.6
    [InlineData(MirrorQuality.Balanced, MirrorCodec.H264, 2500, 1000, 60, 16800, 4200)] // × 1.4
    [InlineData(MirrorQuality.High, MirrorCodec.Hevc, 3840, 2160, 60, 40000, 19907)]    // clamped to 40 Mbps
    [InlineData(MirrorQuality.Low, MirrorCodec.Hevc, 1280, 720, 60, 2212, 2000)]
    [InlineData(MirrorQuality.Balanced, MirrorCodec.Hevc, 2228, 1440, 120, 23342, 5835)]
    [InlineData(MirrorQuality.High, MirrorCodec.H264, 2000, 1250, 90, 40000, 10714)]
    [InlineData(MirrorQuality.Low, MirrorCodec.Hevc, 2, 2, 1, 2000, 2000)]               // clamped to 2 Mbps
    public void Bitrate_scales_the_preset(MirrorQuality quality, MirrorCodec codec, int w, int h, int fps, int target, int floor) =>
        Assert.Equal((target, floor), MirrorStreamPlanner.Bitrate(quality, codec, w, h, fps));

    [Fact]
    public void Bitrate_rejects_bad_inputs()
    {
        Assert.Equal("quality", Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.Bitrate((MirrorQuality)3, MirrorCodec.Hevc, 2, 2, 30)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.Bitrate(MirrorQuality.Low, MirrorCodec.Hevc, 0, 2, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.Bitrate(MirrorQuality.Low, MirrorCodec.Hevc, 2, 2, 0));
    }

    [Fact]
    public void Plan_combines_codec_size_fps_and_bitrate()
    {
        MirrorPlanInputs inputs = Inputs();
        Assert.True(MirrorStreamPlanner.TryPlan(inputs, out MirrorStreamPlan plan, out MirrorReason failure));
        Assert.Equal(MirrorReason.None, failure);
        (int target60, int floor60) = MirrorStreamPlanner.Bitrate(MirrorQuality.Balanced, MirrorCodec.Hevc, 2228, 1440, 60);
        Assert.Equal(new MirrorStreamPlan(MirrorCodec.Hevc, 2228, 1440, 60, target60, floor60, 120), plan); // the S23: 3.2 MP caps 120 → 93, snapped to 60

        Assert.True(MirrorStreamPlanner.TryPlan(inputs with { MaxPixelRate = 400_000_000 }, out plan, out _)); // a measured cap: stays at 120
        Assert.Equal(new MirrorStreamPlan(MirrorCodec.Hevc, 2228, 1440, 120, 23342, 5835, 120), plan);

        Assert.True(MirrorStreamPlanner.TryPlan(inputs with { EncoderMaxFps = 100 }, out plan, out _)); // 100 isn't a divisor of 120
        Assert.Equal(60, plan.Fps);

        Assert.True(MirrorStreamPlanner.TryPlan(inputs with { Encoders = MirrorCodecs.H264, FpsLimit = 60 }, out plan, out _));
        (int target, int floor) = MirrorStreamPlanner.Bitrate(MirrorQuality.Balanced, MirrorCodec.H264, 2228, 1440, 60);
        Assert.Equal(new MirrorStreamPlan(MirrorCodec.H264, 2228, 1440, 60, target, floor, 120), plan);

        Assert.True(MirrorStreamPlanner.TryPlan(inputs with { Source = new MirrorSource(1728, 1117, 60), Quality = MirrorQuality.Low }, out plan, out _));
        Assert.Equal(MirrorCodec.Hevc, plan.Codec);
        Assert.Equal((1728, 1116), (plan.WidthPx, plan.HeightPx));
        Assert.Equal(60, plan.Fps);
    }

    [Theory]
    [InlineData(1920, 1080, 120, 300_000_000, 1920, 1080, 120)] // fits
    [InlineData(2228, 1440, 120, 300_000_000, 2228, 1440, 93)]  // the fps drops first
    [InlineData(2228, 1440, 120, 200_000_000, 2228, 1440, 62)]
    [InlineData(3840, 2160, 120, 300_000_000, 2980, 1676, 60)]  // not below 60, then the size shrinks
    [InlineData(3840, 2160, 30, 300_000_000, 3840, 2160, 30)]   // fits at 30
    [InlineData(3840, 2160, 50, 300_000_000, 3264, 1836, 50)]   // below 60 already: only the size shrinks
    [InlineData(1000, 1000, 120, 10_000_000, 408, 408, 60)]
    [InlineData(3840, 2160, 120, 1, 2, 2, 60)]                  // at least 2×2
    public void Pixel_rate_cap_lowers_fps_to_60_then_the_size(int w, int h, int fps, long cap, int ew, int eh, int efps)
    {
        Assert.Equal((ew, eh, efps), MirrorStreamPlanner.ApplyPixelRate(w, h, fps, cap));
        if (ew > 2)
        {
            Assert.InRange((long)ew * eh * efps, 1, cap);
        }
    }

    [Theory]
    [InlineData(120, 120, 120)]
    [InlineData(93, 120, 60)]
    [InlineData(59, 120, 40)]
    [InlineData(39, 120, 30)]
    [InlineData(30, 120, 30)]
    [InlineData(29, 120, 29)]  // below 30: nothing to snap to
    [InlineData(89, 90, 45)]
    [InlineData(44, 90, 30)]
    [InlineData(40, 100, 40)]  // no divisor of 100 in 30..40: unchanged
    [InlineData(24, 24, 24)]   // a phone below 30: any divisor
    [InlineData(20, 24, 12)]
    [InlineData(60, 0, 60)]    // unknown refresh
    public void Fps_snaps_down_to_a_divisor_of_the_phone_refresh(int fps, int phoneMaxFps, int expected) =>
        Assert.Equal(expected, MirrorStreamPlanner.SnapFps(fps, phoneMaxFps));

    [Fact]
    public void Pixel_rate_cap_rejects_bad_inputs()
    {
        Assert.Equal("maxPixelRate", Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.ApplyPixelRate(2, 2, 1, 0)).ParamName);
        Assert.Equal("fps", Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.ApplyPixelRate(2, 2, 0, 1)).ParamName);
        Assert.Equal("widthPx", Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.ApplyPixelRate(1, 2, 1, 1)).ParamName);
        Assert.Equal("heightPx", Assert.Throws<ArgumentOutOfRangeException>(() => MirrorStreamPlanner.ApplyPixelRate(2, 1, 1, 1)).ParamName);
        Assert.Equal(300_000_000, Inputs().MaxPixelRate);
    }

    [Fact]
    public void Plan_without_a_common_codec_fails_with_the_reason()
    {
        Assert.False(MirrorStreamPlanner.TryPlan(Inputs() with { Encoders = MirrorCodecs.None }, out MirrorStreamPlan plan, out MirrorReason failure));
        Assert.Equal(MirrorReason.NoCommonCodec, failure);
        Assert.Equal(default, plan);
        Assert.Throws<ArgumentNullException>(() => MirrorStreamPlanner.TryPlan(null!, out _, out _));
    }

    [Fact]
    public void An_epoch_config_carries_the_planned_fps_and_target_bitrate()
    {
        var plan = new MirrorStreamPlan(MirrorCodec.Hevc, 2228, 1440, 120, 23342, 5835);
        Assert.Equal(new MirrorStreamConfig(9, MirrorCodec.Hevc, 2228, 1440, 120, 23342), plan.ToConfig(9));
    }

    [Theory]
    [InlineData(3088, 1440, 0, 0, 1, 1, 3088, 1440)]
    [InlineData(3088, 1440, 0.1, 0, 0.7215, 1, 2228, 1440)]
    [InlineData(1440, 3088, 0, 0.2, 1, 0.5, 1440, 1544)]
    [InlineData(100, 100, 0, 0, 0.00001, 0.00001, 1, 1)]
    public void Active_area_pixels(int sw, int sh, double x, double y, double w, double h, int ew, int eh) =>
        Assert.Equal((ew, eh), MirrorStreamPlanner.ActiveAreaPixels(sw, sh, new NormalizedRect(x, y, w, h)));

    [Fact]
    public void Request_and_config_convert_to_and_from_the_wire()
    {
        var request = new MirrorRequest(Both, 3840, 2160, 120);
        MirrorStartMessage start = request.ToMessage(5000);
        Assert.Equal(5000, start.VideoUdpPort);
        Assert.Equal(request, MirrorRequest.FromMessage(start));
        Assert.Throws<ArgumentOutOfRangeException>(() => (request with { MaxWidthPx = 0 }).ToMessage(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => (request with { MaxHeightPx = 65536 }).ToMessage(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => (request with { MaxFps = 256 }).ToMessage(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => request.ToMessage(0));

        var config = new MirrorStreamConfig(4, MirrorCodec.H264, 1920, 1080, 60, 12000);
        MirrorConfigMessage message = config.ToMessage(6000);
        Assert.Equal(6000, message.VideoUdpPort);
        Assert.Equal(config, MirrorStreamConfig.FromMessage(message));
        Assert.Throws<ArgumentOutOfRangeException>(() => (config with { WidthPx = 70000 }).ToMessage(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => (config with { Fps = 0 }).ToMessage(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => (config with { BitrateKbps = 0 }).ToMessage(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => config.ToMessage(0));
        Assert.Equal(int.MaxValue, MirrorStreamConfig.FromMessage(new MirrorConfigMessage(1, MirrorCodec.Hevc, 2, 2, 1, uint.MaxValue, 1)).BitrateKbps);
    }

    private static MirrorPlanInputs Inputs() => new()
    {
        Request = new MirrorRequest(Both, 4096, 4096, 120),
        Encoders = Both,
        Source = new MirrorSource(3456, 2234, 120),
        ActiveWidthPx = 2228,
        ActiveHeightPx = 1440,
    };
}
