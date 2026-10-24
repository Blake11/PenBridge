using Android.Content;
using Android.Hardware.Display;
using Android.Media;
using Android.Views;

namespace PenBridge.Android.Mirror;

/// <summary>The decoder picked for one codec.</summary>
/// <param name="Codec">The codec it decodes.</param>
/// <param name="Name">MediaCodec name (passed to <c>MediaCodec.CreateByCodecName</c>).</param>
/// <param name="Hardware">True for a hardware decoder. Release builds only ever pick hardware decoders.</param>
/// <param name="LowLatency">The decoder advertises <c>FEATURE_LowLatency</c> (API 30+).</param>
/// <param name="MaxWidth">Largest width it decodes at the needed frame rate (see <see cref="MirrorDecoderCapabilities"/>).</param>
/// <param name="MaxHeight">Largest height, as <paramref name="MaxWidth"/>.</param>
/// <param name="MaxFps">Highest frame rate it decodes at that size.</param>
internal sealed record MirrorDecoderInfo(MirrorCodec Codec, string Name, bool Hardware, bool LowLatency, int MaxWidth, int MaxHeight, int MaxFps);

/// <summary>
/// What the phone can decode, for the MirrorStart message (spec "Control messages"): the codec mask, the decoder size
/// limit and the frame-rate limit. Probed once from <see cref="MediaCodecList"/>, hardware decoders only.
/// </summary>
/// <remarks>
/// <para>The size limit is checked against the display, the largest video the Mac ever sends (the active area is part
/// of the screen): the display's long side in both directions, so either orientation fits, scaled down until the
/// decoder supports that size at the display's highest refresh rate (the mirror frame rate follows the refresh rates,
/// so a 120 Hz phone may get 120 fps). With two codecs the smaller limit wins, so the Mac's fallback codec fits too.
/// <see cref="MaxFps"/> is that highest refresh rate (spec: MirrorStart <c>maxFps</c>).</para>
/// <para><b>Debug builds only:</b> when a codec has no hardware decoder (the emulator), a software decoder stands in so
/// the decode and display path can be exercised; <see cref="MirrorDecoderInfo.Hardware"/> is false then. Release
/// builds never offer a software decoder.</para>
/// </remarks>
internal sealed record MirrorDecoderCapabilities(MirrorCodecs Codecs, int MaxWidth, int MaxHeight, int MaxFps, MirrorDecoderInfo? Hevc, MirrorDecoderInfo? H264)
{
    public static MirrorDecoderCapabilities None { get; } = new(MirrorCodecs.None, 0, 0, 0, null, null);

    // MediaCodecInfo.CodecCapabilities FEATURE_* names (platform constants).
    private const string FeatureSecurePlayback = "secure-playback";
    private const string FeatureLowLatency = "low-latency";

    private static readonly Lock CacheLock = new();
    private static MirrorDecoderCapabilities? _cached;

    /// <summary>The probed capabilities, probed on first use and then cached for the process. Any thread.</summary>
    public static MirrorDecoderCapabilities Get(Context context)
    {
        lock (CacheLock)
        {
            return _cached ??= Probe(context.ApplicationContext ?? context);
        }
    }

    /// <summary>The decoder for <paramref name="codec"/>, or null if the phone has none.</summary>
    public MirrorDecoderInfo? For(MirrorCodec codec) => codec == MirrorCodec.Hevc ? Hevc : H264;

    /// <summary>Probes the codecs and the display. Any thread; a few milliseconds, so not on the input path.</summary>
    public static MirrorDecoderCapabilities Probe(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        (int longSide, int shortSide, int refresh) = DisplayLimits(context);
        int fps = Math.Clamp(refresh, 1, byte.MaxValue);

        MediaCodecInfo[] infos;
        try
        {
            using var list = new MediaCodecList(MediaCodecListKind.RegularCodecs);
            infos = list.GetCodecInfos() ?? [];
        }
        catch (Java.Lang.Exception)
        {
            return None;
        }

        MirrorDecoderInfo? hevc = Pick(infos, MirrorCodec.Hevc, longSide, shortSide, fps);
        MirrorDecoderInfo? h264 = Pick(infos, MirrorCodec.H264, longSide, shortSide, fps);
        foreach (MediaCodecInfo info in infos)
        {
            info.Dispose();
        }

        MirrorCodecs codecs = (hevc is null ? MirrorCodecs.None : MirrorCodecs.Hevc) | (h264 is null ? MirrorCodecs.None : MirrorCodecs.H264);
        if (codecs == MirrorCodecs.None)
        {
            return None;
        }

        int maxW = Math.Min(hevc?.MaxWidth ?? int.MaxValue, h264?.MaxWidth ?? int.MaxValue);
        int maxH = Math.Min(hevc?.MaxHeight ?? int.MaxValue, h264?.MaxHeight ?? int.MaxValue);
        return new MirrorDecoderCapabilities(codecs, maxW, maxH, fps, hevc, h264);
    }

    private static MirrorDecoderInfo? Pick(MediaCodecInfo[] infos, MirrorCodec codec, int longSide, int shortSide, int fps)
    {
        MirrorDecoderInfo? hardware = null;
        MirrorDecoderInfo? software = null;
        string mime = codec.Mime();
        foreach (MediaCodecInfo info in infos)
        {
            if (info.IsEncoder || info.IsAlias || !Supports(info, mime) || info.Name is not { } name
                || name.EndsWith(".secure", StringComparison.Ordinal))
            {
                continue;
            }

            bool isHardware = info.IsHardwareAccelerated && !info.IsSoftwareOnly;
            if ((isHardware ? hardware : software) is not null)
            {
                continue; // MediaCodecList lists the preferred decoder first
            }

            MirrorDecoderInfo? candidate = Describe(info, codec, name, isHardware, mime, longSide, shortSide, fps);
            if (isHardware)
            {
                hardware = candidate;
            }
            else
            {
                software = candidate;
            }
        }

#if DEBUG
        // Debug only (emulator): no hardware decoder → a software one, so the path can be exercised. Never in Release.
        return hardware ?? software;
#else
        return hardware;
#endif
    }

    private static bool Supports(MediaCodecInfo info, string mime)
    {
        foreach (string type in info.GetSupportedTypes() ?? [])
        {
            if (string.Equals(type, mime, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static MirrorDecoderInfo? Describe(MediaCodecInfo info, MirrorCodec codec, string name, bool hardware, string mime, int longSide, int shortSide, int fps)
    {
        try
        {
            using MediaCodecInfo.CodecCapabilities? caps = info.GetCapabilitiesForType(mime);
            using MediaCodecInfo.VideoCapabilities? video = caps?.VideoCapabilities;
            if (caps is null || video is null || caps.IsFeatureRequired(FeatureSecurePlayback))
            {
                return null;
            }

            bool lowLatency = OperatingSystem.IsAndroidVersionAtLeast(30)
                && caps.IsFeatureSupported(FeatureLowLatency);

            // Both orientations of the display at the needed rate, scaled down until the decoder takes them.
            for (double scale = 1; scale > 0.2; scale *= 0.9)
            {
                int l = Even(longSide * scale);
                int s = Even(shortSide * scale);
                if (video.AreSizeAndRateSupported(l, s, fps) && video.AreSizeAndRateSupported(s, l, fps))
                {
                    int maxFps = MaxRate(video, l, s);
                    return new MirrorDecoderInfo(codec, name, hardware, lowLatency, l, l, Math.Max(maxFps, fps));
                }
            }

            return null;
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    private static int MaxRate(MediaCodecInfo.VideoCapabilities video, int width, int height)
    {
        using global::Android.Util.Range? rates = video.GetSupportedFrameRatesFor(width, height);
        return rates?.Upper is Java.Lang.Number upper ? (int)Math.Min(upper.DoubleValue(), byte.MaxValue) : 0;
    }

    private static int Even(double v) => Math.Max(2, (int)v & ~1);

    /// <summary>Display long side, short side (physical pixels, current mode) and its highest supported refresh rate.</summary>
    private static (int Long, int Short, int Refresh) DisplayLimits(Context context)
    {
        var dm = context.GetSystemService(Context.DisplayService) as DisplayManager;
        Display? display = dm?.GetDisplay(Display.DefaultDisplay);
        Display.Mode? mode = display?.GetMode();
        if (display is null || mode is null)
        {
            return (1920, 1080, 60);
        }

        int w = mode.PhysicalWidth;
        int h = mode.PhysicalHeight;
        float refresh = mode.RefreshRate;
        foreach (Display.Mode m in display.GetSupportedModes() ?? [])
        {
            refresh = Math.Max(refresh, m.RefreshRate);
        }

        return (Math.Max(w, h), Math.Min(w, h), Math.Max(1, (int)MathF.Round(refresh)));
    }
}
