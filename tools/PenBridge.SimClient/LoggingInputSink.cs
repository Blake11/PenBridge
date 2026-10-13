using System.Globalization;
using PenBridge.Core.Input;

namespace PenBridge.SimClient;

/// <summary>
/// Prints every pen and gesture event the in-process server produces. A demo sink: it formats strings, which the
/// real injector must not do; output goes through a buffered writer so the server's input lock is held briefly.
/// </summary>
internal sealed class LoggingInputSink(TextWriter output) : IInputSink
{
    private long _pen;
    private long _gestures;

    public long PenEvents => Interlocked.Read(ref _pen);

    public long GestureEvents => Interlocked.Read(ref _gestures);

    public void OnPenEvent(in PenEvent e)
    {
        Interlocked.Increment(ref _pen);
        string extra = e.Kind switch
        {
            PenEventKind.ButtonDown or PenEventKind.ButtonUp => " button=" + e.Button,
            PenEventKind.ToolChanged => " from=" + e.PreviousTool,
            _ => string.Empty,
        };
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"pen  {e.Kind,-14} t={e.TimestampUs / 1000.0,9:F1}ms at=({e.Position.X:F3}, {e.Position.Y:F3}) p={e.Sample.Pressure,5} tilt=({e.Sample.TiltX},{e.Sample.TiltY}) rot={e.Sample.Rotation} tool={e.Sample.Tool}{extra}{(e.Synthetic ? " [synthetic]" : string.Empty)}"));
    }

    public void OnGestureEvent(in GestureEvent e)
    {
        Interlocked.Increment(ref _gestures);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"gest {e.Kind,-6} {e.Phase,-9} d=({e.DeltaX:F3}, {e.DeltaY:F3}) scale={e.ScaleDelta:F4} center=({e.Center.X:F3}, {e.Center.Y:F3}){(e.Synthetic ? " [synthetic]" : string.Empty)}"));
    }
}
