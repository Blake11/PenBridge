using Microsoft.Extensions.Logging;

namespace PenBridge.Mac.Mirror;

/// <summary>Source-generated log messages of the mirror pipeline. Nothing here runs per frame.</summary>
internal static partial class MirrorLog
{
    [LoggerMessage(900, LogLevel.Information, "Hardware video encoders: {Codecs}")]
    public static partial void HardwareEncoders(ILogger logger, string codecs);

    [LoggerMessage(901, LogLevel.Debug, "{Codec} encoder refused low-latency rate control (OSStatus {Status}); using the standard real-time mode")]
    public static partial void LowLatencyRefused(ILogger logger, string codec, int status);

    [LoggerMessage(902, LogLevel.Information, "{Codec} encoder {Width}x{Height} @ {Fps} fps, {BitrateKbps} kbps (low latency: {LowLatency})")]
    public static partial void EncoderCreated(ILogger logger, string codec, int width, int height, int fps, int bitrateKbps, bool lowLatency);

    [LoggerMessage(903, LogLevel.Debug, "Encoder property {Key} refused (OSStatus {Status})")]
    public static partial void PropertyRefused(ILogger logger, string key, int status);

    [LoggerMessage(904, LogLevel.Information, "Mirror capture started: {Displays} display(s), rect {X},{Y} {Width}x{Height} pt, stream {StreamWidth}x{StreamHeight} @ {Fps} fps, {Codec}")]
    public static partial void Started(ILogger logger, int displays, double x, double y, double width, double height, int streamWidth, int streamHeight, int fps, string codec);

    [LoggerMessage(905, LogLevel.Information, "Mirror capture stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(906, LogLevel.Warning, "Mirroring failed ({Failure}): {Message}")]
    public static partial void Failed(ILogger logger, string failure, string message);

    [LoggerMessage(907, LogLevel.Warning, "Encoding failed (OSStatus {Status}); recreating the encoder")]
    public static partial void EncodeFailed(ILogger logger, int status);

    [LoggerMessage(908, LogLevel.Information, "Mirror reconfigured: stream {Width}x{Height} @ {Fps} fps, {BitrateKbps} kbps, {Codec} ({How})")]
    public static partial void Reconfigured(ILogger logger, int width, int height, int fps, int bitrateKbps, string codec, string how);

    [LoggerMessage(909, LogLevel.Warning, "PenBridge is not in the shareable content list: its windows can't be excluded from the mirror")]
    public static partial void OwnAppNotFound(ILogger logger);

    [LoggerMessage(910, LogLevel.Information, "Display refresh rates changed")]
    public static partial void RefreshChanged(ILogger logger);

    [LoggerMessage(911, LogLevel.Information, "Mirror dump: {Frames} frames ({Keyframes} keyframes, {Bytes} bytes) to {Path}; capture→encoded latency p50 {P50Ms:F2} ms, p95 {P95Ms:F2} ms, max {MaxMs:F2} ms; CPU {CpuPercent:F1} %")]
    public static partial void DumpFinished(ILogger logger, int frames, int keyframes, long bytes, string path, double p50Ms, double p95Ms, double maxMs, double cpuPercent);

    [LoggerMessage(912, LogLevel.Warning, "Mirror dump could not start: {Reason}")]
    public static partial void DumpFailed(ILogger logger, string reason);
}
