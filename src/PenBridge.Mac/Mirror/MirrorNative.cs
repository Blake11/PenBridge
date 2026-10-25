using System.Runtime.InteropServices;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// CoreMedia, CoreVideo, CoreFoundation, VideoToolbox and CoreGraphics calls of the mirror pipeline that go through raw
/// handles, so the per-frame path (capture callback → encode → encoder callback → Annex-B) creates no managed wrapper.
/// Off the per-frame path the bindings are used (ScreenCaptureKit objects, the bound VideoToolbox key constants).
/// </summary>
/// <remarks>
/// Signatures are blittable only (pointers, integers, the 24-byte <see cref="CMTimeValue"/>), so no runtime marshalling is
/// involved and they are NativeAOT/trim safe. Unbound constants are read with <see cref="NativeLibrary"/> (not
/// <c>ObjCRuntime.Dlfcn.dlopen</c>, which the NativeAOT build stubs out: Trimming.Substitutions.xml).
/// </remarks>
internal static unsafe class MirrorNative
{
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreMediaLib = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string CoreVideoLib = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string VideoToolboxLib = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <summary>'avc1'.</summary>
    internal const uint CodecH264 = 0x61766331;

    /// <summary>'hvc1'.</summary>
    internal const uint CodecHevc = 0x68766331;

    /// <summary>kCFNumberSInt64Type.</summary>
    internal const int CFNumberSInt64Type = 4;

    /// <summary>kCFNumberDoubleType.</summary>
    internal const int CFNumberDoubleType = 13;

    /// <summary>kVTEncodeInfo_FrameDropped.</summary>
    internal const uint EncodeInfoFrameDropped = 1 << 1;

    /// <summary><c>CMTime</c>: value / timescale seconds.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CMTimeValue
    {
        public long Value;
        public int Timescale;
        public uint Flags;
        public long Epoch;

        /// <summary>kCMTimeFlags_Valid.</summary>
        public const uint FlagValid = 1;

        public static CMTimeValue Invalid => default;

        public static CMTimeValue FromMicroseconds(long us) => new() { Value = us, Timescale = 1_000_000, Flags = FlagValid };

        public readonly bool IsValid => (Flags & FlagValid) != 0 && Timescale > 0;

        /// <summary>The time in microseconds (valid times only).</summary>
        public readonly long ToMicroseconds() => Timescale == 1_000_000 ? Value : (long)(Value * (1_000_000.0 / Timescale));
    }

    private static readonly Lazy<IntPtr> VideoToolboxHandle = new(() => NativeLibrary.Load(VideoToolboxLib));
    private static readonly Lazy<IntPtr> CoreFoundationHandle = new(() => NativeLibrary.Load(CoreFoundationLib));
    private static readonly Lazy<IntPtr> CoreMediaHandle = new(() => NativeLibrary.Load(CoreMediaLib));

    /// <summary>kCFBooleanTrue.</summary>
    internal static IntPtr True => ReadConstant(CoreFoundationHandle.Value, "kCFBooleanTrue");

    /// <summary>kCFBooleanFalse.</summary>
    internal static IntPtr False => ReadConstant(CoreFoundationHandle.Value, "kCFBooleanFalse");

    /// <summary>A CFStringRef constant exported by VideoToolbox, or 0 when this macOS doesn't have it.</summary>
    internal static IntPtr VideoToolboxConstant(string name) => ReadConstant(VideoToolboxHandle.Value, name);

    /// <summary>A CFStringRef constant exported by CoreMedia, or 0.</summary>
    internal static IntPtr CoreMediaConstant(string name) => ReadConstant(CoreMediaHandle.Value, name);

    private static IntPtr ReadConstant(IntPtr library, string name)
        => NativeLibrary.TryGetExport(library, name, out IntPtr address) ? *(IntPtr*)address : IntPtr.Zero;

    /// <summary>Pixels per point of a display (2 on Retina), from its current mode; 1 when unknown.</summary>
    internal static double BackingScale(uint displayId, double widthPoints)
    {
        IntPtr mode = CGDisplayCopyDisplayMode(displayId);
        if (mode == IntPtr.Zero)
        {
            return 1;
        }

        try
        {
            double pixels = CGDisplayModeGetPixelWidth(mode);
            double points = widthPoints > 0 ? widthPoints : CGDisplayModeGetWidth(mode);
            return points > 0 && pixels > 0 ? pixels / points : 1;
        }
        finally
        {
            CGDisplayModeRelease(mode);
        }
    }

    /// <summary>The current CoreGraphics display mode's refresh rate in Hz; 0 when it doesn't report one.</summary>
    internal static double ModeRefreshRate(uint displayId)
    {
        IntPtr mode = CGDisplayCopyDisplayMode(displayId);
        if (mode == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return CGDisplayModeGetRefreshRate(mode);
        }
        finally
        {
            CGDisplayModeRelease(mode);
        }
    }

    // CoreFoundation
    [DllImport(CoreFoundationLib)]
    internal static extern IntPtr CFRetain(IntPtr cf);

    [DllImport(CoreFoundationLib)]
    internal static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundationLib)]
    internal static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundationLib)]
    internal static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundationLib)]
    internal static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    /// <summary>C <c>Boolean</c> as a byte.</summary>
    [DllImport(CoreFoundationLib)]
    internal static extern byte CFNumberGetValue(IntPtr number, int type, long* value);

    [DllImport(CoreFoundationLib, EntryPoint = "CFNumberGetValue")]
    internal static extern byte CFNumberGetDouble(IntPtr number, int type, double* value);

    [DllImport(CoreFoundationLib)]
    internal static extern byte CFBooleanGetValue(IntPtr boolean);

    [DllImport(CoreFoundationLib)]
    internal static extern nuint CFBooleanGetTypeID();

    [DllImport(CoreFoundationLib)]
    internal static extern nuint CFGetTypeID(IntPtr cf);

    // CoreMedia
    [DllImport(CoreMediaLib)]
    internal static extern IntPtr CMSampleBufferGetImageBuffer(IntPtr sbuf);

    [DllImport(CoreMediaLib)]
    internal static extern CMTimeValue CMSampleBufferGetPresentationTimeStamp(IntPtr sbuf);

    [DllImport(CoreMediaLib)]
    internal static extern IntPtr CMSampleBufferGetSampleAttachmentsArray(IntPtr sbuf, byte createIfNecessary);

    [DllImport(CoreMediaLib)]
    internal static extern IntPtr CMSampleBufferGetDataBuffer(IntPtr sbuf);

    [DllImport(CoreMediaLib)]
    internal static extern IntPtr CMSampleBufferGetFormatDescription(IntPtr sbuf);

    [DllImport(CoreMediaLib)]
    internal static extern int CMBlockBufferGetDataPointer(IntPtr buffer, nuint offset, nuint* lengthAtOffset, nuint* totalLength, byte** dataPointer);

    [DllImport(CoreMediaLib)]
    internal static extern int CMBlockBufferCopyDataBytes(IntPtr buffer, nuint offset, nuint length, byte* destination);

    [DllImport(CoreMediaLib)]
    internal static extern int CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
        IntPtr format, nuint index, byte** parameterSet, nuint* size, nuint* count, int* nalHeaderLength);

    [DllImport(CoreMediaLib)]
    internal static extern int CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
        IntPtr format, nuint index, byte** parameterSet, nuint* size, nuint* count, int* nalHeaderLength);

    // CoreVideo
    [DllImport(CoreVideoLib)]
    internal static extern nuint CVPixelBufferGetWidth(IntPtr pixelBuffer);

    [DllImport(CoreVideoLib)]
    internal static extern nuint CVPixelBufferGetHeight(IntPtr pixelBuffer);

    [DllImport(CoreVideoLib)]
    internal static extern void CVBufferPropagateAttachments(IntPtr sourceBuffer, IntPtr destinationBuffer);

    // VideoToolbox
    [DllImport(VideoToolboxLib)]
    internal static extern int VTCompressionSessionCreate(
        IntPtr allocator,
        int width,
        int height,
        uint codecType,
        IntPtr encoderSpecification,
        IntPtr sourceImageBufferAttributes,
        IntPtr compressedDataAllocator,
        delegate* unmanaged<IntPtr, IntPtr, int, uint, IntPtr, void> outputCallback,
        IntPtr outputCallbackRefCon,
        IntPtr* compressionSessionOut);

    [DllImport(VideoToolboxLib)]
    internal static extern int VTSessionSetProperty(IntPtr session, IntPtr key, IntPtr value);

    [DllImport(VideoToolboxLib)]
    internal static extern int VTSessionCopyProperty(IntPtr session, IntPtr key, IntPtr allocator, IntPtr* value);

    [DllImport(VideoToolboxLib)]
    internal static extern int VTCompressionSessionPrepareToEncodeFrames(IntPtr session);

    [DllImport(VideoToolboxLib)]
    internal static extern int VTCompressionSessionEncodeFrame(
        IntPtr session, IntPtr imageBuffer, CMTimeValue presentationTimeStamp, CMTimeValue duration, IntPtr frameProperties,
        IntPtr sourceFrameRefcon, uint* infoFlagsOut);

    [DllImport(VideoToolboxLib)]
    internal static extern int VTCompressionSessionCompleteFrames(IntPtr session, CMTimeValue completeUntilPresentationTimeStamp);

    [DllImport(VideoToolboxLib)]
    internal static extern void VTCompressionSessionInvalidate(IntPtr session);

    // CoreGraphics
    /// <summary><c>bool CGPreflightScreenCaptureAccess(void)</c>: Screen Recording granted, without prompting.</summary>
    [DllImport(CoreGraphicsLib)]
    internal static extern byte CGPreflightScreenCaptureAccess();

    /// <summary><c>bool CGRequestScreenCaptureAccess(void)</c>: prompts once per app; false until granted (may need a relaunch).</summary>
    [DllImport(CoreGraphicsLib)]
    internal static extern byte CGRequestScreenCaptureAccess();

    [DllImport(CoreGraphicsLib)]
    private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphicsLib)]
    private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);

    [DllImport(CoreGraphicsLib)]
    private static extern nuint CGDisplayModeGetWidth(IntPtr mode);

    [DllImport(CoreGraphicsLib)]
    private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);

    [DllImport(CoreGraphicsLib)]
    private static extern void CGDisplayModeRelease(IntPtr mode);
}
