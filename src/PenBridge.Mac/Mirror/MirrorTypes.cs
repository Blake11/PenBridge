using PenBridge.Core.Input;
using PenBridge.Mac.Input;

namespace PenBridge.Mac.Mirror;

/// <summary>A video codec of the mirror stream; the values are the wire values of MirrorConfig.codec (ADR-0038).</summary>
public enum MirrorCodec : byte
{
    H264 = 1,
    Hevc = 2,
}

/// <summary>A set of codecs; the bits are the wire bits of MirrorStart.codecs (bit 0 H.264, bit 1 HEVC).</summary>
[Flags]
public enum MirrorCodecs : byte
{
    None = 0,
    H264 = 1,
    Hevc = 2,
}

/// <summary>Why mirroring could not start or stopped by itself.</summary>
public enum MirrorFailure
{
    /// <summary>Screen Recording is not granted (MirrorReason PermissionDenied).</summary>
    PermissionDenied,

    /// <summary>ScreenCaptureKit could not start or stopped the stream (display gone, system stop; CaptureFailed).</summary>
    CaptureFailed,

    /// <summary>
    /// The hardware encoder for the requested codec could not be created or failed; with HEVC the caller falls back to
    /// H.264 (new epoch).
    /// </summary>
    EncoderFailed,
}

/// <summary>Receives encoded frames. Called on a VideoToolbox thread, below input priority, one frame at a time.</summary>
public interface IMirrorFrameSink
{
    /// <summary>
    /// One Annex-B access unit. Keyframes start with the parameter sets (VPS/SPS/PPS for HEVC, SPS/PPS for H.264). The
    /// span is only valid during the call (the buffer is reused for the next frame): copy or send it before returning,
    /// and don't block. Don't call any <see cref="MirrorController"/> control method (start, reconfigure, stop, dispose)
    /// from here: they wait for the encoder to drain, which is waiting for this call, so they deadlock. <c>RequestKeyframe</c>
    /// is safe (it only queues work).
    /// </summary>
    /// <param name="accessUnit">The access unit, start codes included.</param>
    /// <param name="keyframe">True for an IDR/IRAP frame.</param>
    /// <param name="captureTimeUs">
    /// When ScreenCaptureKit captured the frame, in microseconds of the host monotonic clock
    /// (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> divided down to µs; CLOCK_UPTIME_RAW). Convert it to the
    /// session clock by subtracting the session epoch.
    /// </param>
    void OnFrame(ReadOnlySpan<byte> accessUnit, bool keyframe, long captureTimeUs);
}

/// <summary>The encoder side of a stream: size in pixels (even), frame rate, average bitrate and codec.</summary>
public readonly record struct MirrorStreamSettings(int Width, int Height, int Fps, int BitrateKbps, MirrorCodec Codec)
{
    /// <summary>Throws when the values can't be encoded (odd or tiny size, fps outside 1..240, no bitrate).</summary>
    internal void Validate()
    {
        if (Width < 16 || Height < 16 || Width > 8192 || Height > 8192 || (Width & 1) != 0 || (Height & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), $"Stream size {Width}x{Height} must be even, 16..8192.");
        }

        if (Fps is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(Fps), $"Fps {Fps} must be 1..240.");
        }

        if (BitrateKbps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BitrateKbps), "Bitrate must be positive.");
        }

        if (Codec is not (MirrorCodec.H264 or MirrorCodec.Hevc))
        {
            throw new ArgumentOutOfRangeException(nameof(Codec), "Unknown codec.");
        }
    }
}

/// <summary>A display to capture: its CGDirectDisplayID and bounds in global display points (top-left origin, y down).</summary>
public readonly record struct MirrorDisplay(uint DisplayId, DisplayRect Bounds);

/// <summary>
/// What to capture (ADR-0038 "Content"): one display with a rectangle of it (the whole display, a region, or a window's
/// clipped screen rect, ADR-0031/0036), or all displays composited into their bounding box. Immutable.
/// </summary>
public sealed class MirrorTarget
{
    private readonly MirrorDisplay[] _displays;

    private MirrorTarget(MirrorDisplay[] displays, DisplayRect rect, bool all)
    {
        _displays = displays;
        Rect = rect;
        IsAllDisplays = all;
    }

    /// <summary>The displays captured: one, or all of them.</summary>
    public ReadOnlySpan<MirrorDisplay> Displays => _displays;

    /// <summary>The captured rectangle in global display points: inside the display, or the bounding box of all of them.</summary>
    public DisplayRect Rect { get; }

    /// <summary>True for all displays (one stream per display, composited).</summary>
    public bool IsAllDisplays { get; }

    /// <summary>One display; <paramref name="rect"/> (global points) is clipped to it.</summary>
    /// <exception cref="ArgumentException">The rectangle doesn't overlap the display.</exception>
    public static MirrorTarget ForDisplay(MirrorDisplay display, DisplayRect rect)
    {
        DisplayRect d = display.Bounds;
        double left = Math.Max(rect.X, d.X);
        double top = Math.Max(rect.Y, d.Y);
        double right = Math.Min(rect.X + rect.Width, d.X + d.Width);
        double bottom = Math.Min(rect.Y + rect.Height, d.Y + d.Height);
        if (!(right - left >= 1) || !(bottom - top >= 1))
        {
            throw new ArgumentException("The rectangle doesn't overlap the display.", nameof(rect));
        }

        return new MirrorTarget([display], new DisplayRect(left, top, right - left, bottom - top), all: false);
    }

    /// <summary>All displays as one desktop (their bounding box, gaps black).</summary>
    /// <exception cref="ArgumentException">No displays.</exception>
    public static MirrorTarget ForAllDisplays(ReadOnlySpan<MirrorDisplay> displays)
    {
        if (displays.IsEmpty || displays.Length > MappingTarget.MaxDisplays)
        {
            throw new ArgumentException("Needs at least one display.", nameof(displays));
        }

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (MirrorDisplay m in displays)
        {
            left = Math.Min(left, m.Bounds.X);
            top = Math.Min(top, m.Bounds.Y);
            right = Math.Max(right, m.Bounds.X + m.Bounds.Width);
            bottom = Math.Max(bottom, m.Bounds.Y + m.Bounds.Height);
        }

        return new MirrorTarget(displays.ToArray(), new DisplayRect(left, top, right - left, bottom - top), all: true);
    }

    /// <summary>
    /// The mirror target for the pen's mapping target: the same rectangle (display, region or window rect) or all
    /// displays. Displays are matched by bounds against <paramref name="displays"/> (the resolver's list).
    /// </summary>
    /// <returns>Null when a display of the mapping isn't in the list (reconfiguration in progress).</returns>
    public static MirrorTarget? FromMapping(MappingTarget mapping, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(displays);
        ReadOnlySpan<DisplayRect> rects = mapping.Displays;
        var found = new MirrorDisplay[rects.Length];
        for (int i = 0; i < rects.Length; i++)
        {
            DisplayInfo? info = null;
            foreach (DisplayInfo d in displays)
            {
                if (d.Bounds == rects[i])
                {
                    info = d;
                    break;
                }
            }

            if (info is null)
            {
                return null;
            }

            found[i] = new MirrorDisplay(info.Id, info.Bounds);
        }

        return mapping.IsAllDisplays ? ForAllDisplays(found) : ForDisplay(found[0], mapping.Bounds);
    }

    /// <summary>
    /// The source's size in pixels (the captured rectangle at its display's backing scale; the largest scale with all
    /// displays). The stream size is capped at this (spec "Size"). Any thread.
    /// </summary>
    public (int Width, int Height) SourcePixelSize()
    {
        double scale = 1;
        foreach (MirrorDisplay d in _displays)
        {
            scale = Math.Max(scale, MirrorNative.BackingScale(d.DisplayId, d.Bounds.Width));
        }

        return ((int)Math.Round(Rect.Width * scale), (int)Math.Round(Rect.Height * scale));
    }

    /// <summary>True when both capture the same displays and rectangle.</summary>
    internal bool SameAs(MirrorTarget? other)
    {
        if (other is null || other.IsAllDisplays != IsAllDisplays || other.Rect != Rect || other._displays.Length != _displays.Length)
        {
            return false;
        }

        for (int i = 0; i < _displays.Length; i++)
        {
            if (_displays[i] != other._displays[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when both use the same displays (only the rectangle differs), so the streams can be updated in place.</summary>
    internal bool SameDisplays(MirrorTarget other)
    {
        if (other.IsAllDisplays != IsAllDisplays || other._displays.Length != _displays.Length)
        {
            return false;
        }

        for (int i = 0; i < _displays.Length; i++)
        {
            if (_displays[i] != other._displays[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Thrown by <see cref="MirrorController"/> when capture or encode can't start.</summary>
public sealed class MirrorException : Exception
{
    public MirrorException()
    {
    }

    public MirrorException(string message)
        : base(message)
    {
    }

    public MirrorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MirrorException(MirrorFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Which part failed.</summary>
    public MirrorFailure Failure { get; }
}

/// <summary>Event data for <see cref="MirrorController.Failed"/>.</summary>
public sealed class MirrorFailedEventArgs(MirrorFailure failure, string message) : EventArgs
{
    public MirrorFailure Failure { get; } = failure;

    public string Message { get; } = message;
}
