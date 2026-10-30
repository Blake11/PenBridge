using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Mirror;

/// <summary>Everything the planner needs (<see cref="MirrorStreamPlanner.TryPlan"/>).</summary>
public sealed record MirrorPlanInputs
{
    /// <summary>The phone's MirrorStart.</summary>
    public required MirrorRequest Request { get; init; }

    /// <summary>The Mac's hardware encoders (<c>SetMirrorEncoders</c>); drop HEVC here when its session failed to start.</summary>
    public required MirrorCodecs Encoders { get; init; }

    /// <summary>The current target's capture source (<c>SetMirrorSource</c>).</summary>
    public required MirrorSource Source { get; init; }

    /// <summary>Active-area width in phone pixels (<see cref="MirrorStreamPlanner.ActiveAreaPixels"/>).</summary>
    public required int ActiveWidthPx { get; init; }

    /// <summary>Active-area height in phone pixels.</summary>
    public required int ActiveHeightPx { get; init; }

    /// <summary>The MirrorQuality setting.</summary>
    public MirrorQuality Quality { get; init; } = MirrorQuality.Balanced;

    /// <summary>The MirrorFps setting: <see cref="Settings.MacSettings.MirrorFpsAuto"/> (0) or a limit (30, 60, 120).</summary>
    public int FpsLimit { get; init; }

    /// <summary>The encoder's highest frame rate at this size, 0 when unknown (no cap).</summary>
    public int EncoderMaxFps { get; init; }

    /// <summary>
    /// The encoder's pixel rate (fps × width × height) cap in pixels per second, at least 1:
    /// <see cref="MirrorStreamPlanner.DefaultMaxPixelRate"/> unless the head measured its encoder
    /// (<see cref="MirrorStreamPlanner.ApplyPixelRate"/>).
    /// </summary>
    public long MaxPixelRate { get; init; } = MirrorStreamPlanner.DefaultMaxPixelRate;
}

/// <summary>
/// The planner's result before the input guard: the stream at full quality plus the guard's bitrate floor.
/// <see cref="ToConfig"/> gives an epoch's configuration (the planned values); the guard's level only changes the rate
/// the epoch runs at.
/// </summary>
/// <param name="Codec">The chosen codec.</param>
/// <param name="WidthPx">Stream width, even.</param>
/// <param name="HeightPx">Stream height, even.</param>
/// <param name="Fps">Planned frame rate (before the guard).</param>
/// <param name="TargetBitrateKbps">The preset's bitrate for this size, fps and codec.</param>
/// <param name="FloorBitrateKbps">The lowest bitrate the guard steps down to, at most the target.</param>
/// <param name="PhoneMaxFps">
/// The phone's refresh rate (MirrorStart <c>maxFps</c>); the guard's fps steps snap to its divisors
/// (<see cref="MirrorStreamPlanner.SnapFps"/>). 0 = unknown: plain halving.
/// </param>
public readonly record struct MirrorStreamPlan(MirrorCodec Codec, int WidthPx, int HeightPx, int Fps, int TargetBitrateKbps, int FloorBitrateKbps, int PhoneMaxFps = 0)
{
    /// <summary>The configuration for one epoch: the planned fps and the target bitrate.</summary>
    public MirrorStreamConfig ToConfig(byte epoch) =>
        new(epoch, Codec, WidthPx, HeightPx, Fps, TargetBitrateKbps);
}

/// <summary>
/// The Mac's stream planning (<c>docs/specs/screen-mirroring.md</c> "Codec choice", "Size", "Quality presets"). Pure
/// functions; the server replans on every change of an input (target, region, window size, active area, source,
/// quality, fps, encoders) and sends a new MirrorConfig when the result differs.
/// </summary>
public static class MirrorStreamPlanner
{
    /// <summary>Longest stream side.</summary>
    public const int MaxLongSidePx = 3840;

    /// <summary>Shortest stream side's cap (3840×2160 in either orientation).</summary>
    public const int MaxShortSidePx = 2160;

    /// <summary>The pixel count the presets are given for (2.5 MP).</summary>
    public const double ReferencePixels = 2_500_000;

    /// <summary>The frame rate the presets are given for.</summary>
    public const double ReferenceFps = 60;

    /// <summary>Exponent of the fps scaling: × (fps / 60)^0.6.</summary>
    public const double FpsExponent = 0.6;

    /// <summary>H.264 needs this much more bitrate than HEVC for the same quality.</summary>
    public const double H264Factor = 1.4;

    /// <summary>
    /// The default encoder pixel-rate cap, 300 MP/s (fps × width × height). Measured on the dev Mac in low-latency mode:
    /// 120 fps holds up to about 2.5 MP, 4K tops out near 50 fps.
    /// </summary>
    public const long DefaultMaxPixelRate = 300_000_000;

    /// <summary>The pixel-rate cap lowers the fps down to this (or the planned fps if lower) before it shrinks the size.</summary>
    public const int MinPixelRateFps = 60;

    /// <summary>Snapping never takes the fps below this (unless the phone's refresh rate is lower).</summary>
    public const int MinSnapFps = 30;

    /// <summary>Lowest planned bitrate.</summary>
    public const int MinBitrateKbps = 2_000;

    /// <summary>Highest planned bitrate.</summary>
    public const int MaxBitrateKbps = 40_000;

    /// <summary>
    /// The best codec in <c>phone ∩ mac</c>: HEVC first, then H.264; null when they share none (the Mac answers
    /// <c>Unavailable/NoCommonCodec</c>). Unknown bits are ignored.
    /// </summary>
    public static MirrorCodec? ChooseCodec(MirrorCodecs phone, MirrorCodecs mac)
    {
        MirrorCodecs common = phone & mac;
        return (common & MirrorCodecs.Hevc) != 0 ? MirrorCodec.Hevc
            : (common & MirrorCodecs.H264) != 0 ? MirrorCodec.H264
            : null;
    }

    /// <summary>
    /// The active area in phone pixels: the SurfaceInfo size × the ActiveArea fractions, rounded, at least 1×1.
    /// </summary>
    public static (int WidthPx, int HeightPx) ActiveAreaPixels(int surfaceWidthPx, int surfaceHeightPx, NormalizedRect activeArea)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(surfaceWidthPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(surfaceHeightPx, 1);
        int w = (int)Math.Clamp(Math.Round(surfaceWidthPx * activeArea.Width), 1, surfaceWidthPx);
        int h = (int)Math.Clamp(Math.Round(surfaceHeightPx * activeArea.Height), 1, surfaceHeightPx);
        return (w, h);
    }

    /// <summary>
    /// The stream size: the active area scaled down (never up) so it fits the source's pixel size, the phone's decoder
    /// limit and 3840×2160 (long side ≤ 3840, short side ≤ 2160), keeping the aspect ratio; then each side rounded down
    /// to an even number, at least 2. Exact integer arithmetic, so a side that hits a cap equals it (rounded to even).
    /// </summary>
    public static (int WidthPx, int HeightPx) StreamSize(int activeWidthPx, int activeHeightPx, MirrorSource source, int maxWidthPx, int maxHeightPx)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(activeWidthPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(activeHeightPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(source.WidthPx, 1, nameof(source));
        ArgumentOutOfRangeException.ThrowIfLessThan(source.HeightPx, 1, nameof(source));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidthPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeightPx, 1);

        bool landscape = activeWidthPx >= activeHeightPx;
        int capW = landscape ? MaxLongSidePx : MaxShortSidePx;
        int capH = landscape ? MaxShortSidePx : MaxLongSidePx;

        // Scale = num / den, the smallest of 1 and every cap / side.
        long num = 1, den = 1;
        Min(ref num, ref den, source.WidthPx, activeWidthPx);
        Min(ref num, ref den, source.HeightPx, activeHeightPx);
        Min(ref num, ref den, maxWidthPx, activeWidthPx);
        Min(ref num, ref den, maxHeightPx, activeHeightPx);
        Min(ref num, ref den, capW, activeWidthPx);
        Min(ref num, ref den, capH, activeHeightPx);

        int w = Even(activeWidthPx * num / den);
        int h = Even(activeHeightPx * num / den);
        return (w, h);

        static void Min(ref long num, ref long den, long cap, long side)
        {
            if (cap * den < num * side)
            {
                num = cap;
                den = side;
            }
        }

        static int Even(long value) => (int)Math.Max(2, value & ~1L);
    }

    /// <summary>
    /// The planned frame rate: <c>min(source refresh, phone maxFps)</c>, at most the MirrorFps limit when one is set
    /// (<paramref name="fpsLimit"/> &gt; 0) and the encoder's capability when known (<paramref name="encoderMaxFps"/> &gt; 0);
    /// 1..255.
    /// </summary>
    public static int Fps(int sourceRefreshHz, int phoneMaxFps, int fpsLimit, int encoderMaxFps)
    {
        int fps = Math.Min(Math.Max(1, sourceRefreshHz), Math.Max(1, phoneMaxFps));
        if (fpsLimit > 0)
        {
            fps = Math.Min(fps, fpsLimit);
        }

        if (encoderMaxFps > 0)
        {
            fps = Math.Min(fps, encoderMaxFps);
        }

        return Math.Clamp(fps, 1, byte.MaxValue);
    }

    /// <summary>
    /// Fits a stream into the encoder's pixel rate: while fps × width × height exceeds <paramref name="maxPixelRate"/>,
    /// first the fps drops (to the highest rate that fits, but not below <see cref="MinPixelRateFps"/> or the planned fps
    /// if that is lower), then the size shrinks at that fps, keeping the aspect ratio, each side even and at least 2.
    /// </summary>
    public static (int WidthPx, int HeightPx, int Fps) ApplyPixelRate(int widthPx, int heightPx, int fps, long maxPixelRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(widthPx, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(heightPx, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPixelRate, 1);

        long pixels = (long)widthPx * heightPx;
        if (pixels * fps <= maxPixelRate)
        {
            return (widthPx, heightPx, fps);
        }

        int minFps = Math.Min(fps, MinPixelRateFps);
        fps = (int)Math.Max(minFps, maxPixelRate / pixels);
        if (pixels * fps <= maxPixelRate)
        {
            return (widthPx, heightPx, fps);
        }

        double scale = Math.Sqrt((double)maxPixelRate / fps / pixels);
        int w = Even((long)(widthPx * scale));
        int h = Even((long)(heightPx * scale));

        // Guard against rounding: shrink the longer side until it fits (or both are at the minimum).
        while ((long)w * h * fps > maxPixelRate && (w > 2 || h > 2))
        {
            if (w >= h && w > 2)
            {
                w -= 2;
            }
            else
            {
                h -= 2;
            }
        }

        return (w, h, fps);

        static int Even(long value) => (int)Math.Max(2, value & ~1L);
    }

    /// <summary>
    /// Snaps <paramref name="fps"/> down to a divisor of the phone's refresh rate (<c>maxFps / k</c>: 120 → 120, 60,
    /// 40, 30; 90 → 90, 45, 30), so frames land on the panel's refreshes evenly. Never below <see cref="MinSnapFps"/>
    /// (any divisor when the refresh rate itself is lower); when no divisor lies between that and
    /// <paramref name="fps"/>, the fps stays as it is.
    /// </summary>
    public static int SnapFps(int fps, int phoneMaxFps)
    {
        if (fps < 1 || phoneMaxFps < 1)
        {
            return fps;
        }

        int floor = phoneMaxFps < MinSnapFps ? 1 : MinSnapFps;
        for (int k = 1; k <= phoneMaxFps; k++)
        {
            if (phoneMaxFps % k != 0)
            {
                continue;
            }

            int divisor = phoneMaxFps / k;
            if (divisor < floor)
            {
                break;
            }

            if (divisor <= fps)
            {
                return divisor;
            }
        }

        return fps;
    }

    /// <summary>
    /// The preset's target and floor for a stream: preset (at 60 fps and 2.5 MP) × (fps / 60)^0.6 × (pixels / 2.5 MP),
    /// × 1.4 for H.264, then clamped to 2–40 Mbps. The floor is scaled and clamped the same way and is at most the target.
    /// </summary>
    public static (int TargetKbps, int FloorKbps) Bitrate(MirrorQuality quality, MirrorCodec codec, int widthPx, int heightPx, int fps)
    {
        (int target, int floor) = quality switch
        {
            MirrorQuality.Low => (6_000, 2_000),
            MirrorQuality.Balanced => (12_000, 3_000),
            MirrorQuality.High => (24_000, 6_000),
            _ => throw new ArgumentOutOfRangeException(nameof(quality)),
        };
        ArgumentOutOfRangeException.ThrowIfLessThan(widthPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(heightPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);

        double factor = Math.Pow(fps / ReferenceFps, FpsExponent) * ((double)widthPx * heightPx / ReferencePixels);
        if (codec == MirrorCodec.H264)
        {
            factor *= H264Factor;
        }

        int t = Scale(target, factor);
        int f = Math.Min(t, Scale(floor, factor));
        return (t, f);

        static int Scale(int kbps, double factor) => (int)Math.Clamp(Math.Round(kbps * factor), MinBitrateKbps, MaxBitrateKbps);
    }

    /// <summary>
    /// Plans the stream: size, fps, the pixel-rate cap, the fps snapped to the phone's refresh, then the bitrate. False with <paramref name="failure"/> =
    /// <see cref="MirrorReason.NoCommonCodec"/> when the phone's decoders and the Mac's encoders share no codec. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for sizes, rates or a pixel-rate cap below 1 (the server plans only once
    /// it has a SurfaceInfo, an active area and a source).
    /// </summary>
    public static bool TryPlan(MirrorPlanInputs inputs, out MirrorStreamPlan plan, out MirrorReason failure)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        plan = default;
        failure = MirrorReason.None;
        if (ChooseCodec(inputs.Request.Codecs, inputs.Encoders) is not { } codec)
        {
            failure = MirrorReason.NoCommonCodec;
            return false;
        }

        (int w, int h) = StreamSize(inputs.ActiveWidthPx, inputs.ActiveHeightPx, inputs.Source, inputs.Request.MaxWidthPx, inputs.Request.MaxHeightPx);
        int fps = Fps(inputs.Source.RefreshHz, inputs.Request.MaxFps, inputs.FpsLimit, inputs.EncoderMaxFps);
        (w, h, fps) = ApplyPixelRate(w, h, fps, inputs.MaxPixelRate);
        fps = SnapFps(fps, inputs.Request.MaxFps);
        (int target, int floor) = Bitrate(inputs.Quality, codec, w, h, fps);
        plan = new MirrorStreamPlan(codec, w, h, fps, target, floor, inputs.Request.MaxFps);
        return true;
    }
}
