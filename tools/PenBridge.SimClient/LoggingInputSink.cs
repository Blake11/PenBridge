using System.Globalization;
using PenBridge.Core.Input;

namespace PenBridge.SimClient;

/// <summary>
/// Prints every pen and gesture event the in-process server produces, and the momentum a scroll release would
/// coast (simulated at once with <see cref="ScrollMomentum"/>). A demo sink: it formats strings, which the real
/// injector must not do; output goes through a buffered writer so the server's input lock is held briefly.
/// </summary>
internal sealed class LoggingInputSink(TextWriter output) : IInputSink
{
    private long _pen;
    private readonly ScrollMomentum _momentum = new();
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
            $"gest {e.Kind,-11} {e.Phase,-9} t={e.TimestampUs / 1000.0,9:F1}ms d=({e.DeltaX:F3}, {e.DeltaY:F3}) scale={e.ScaleDelta:F4} rot={e.RotationDelta:F2}deg{(e.Direction != GestureDirection.None ? string.Create(CultureInfo.InvariantCulture, $" dir={e.Direction} progress={e.Progress:F3}") : string.Empty)} center=({e.Center.X:F3}, {e.Center.Y:F3}){(e.ClickCount > 0 ? string.Create(CultureInfo.InvariantCulture, $" clicks={e.ClickCount}") : string.Empty)}{(e.Synthetic ? " [synthetic]" : string.Empty)}"));
        if (_momentum.Observe(e))
        {
            double vx = _momentum.ReleaseVelocityX;
            double vy = _momentum.ReleaseVelocityY;
            long duration = _momentum.DurationUs;
            long tick = _momentum.Options.TickInterval.Ticks / TimeSpan.TicksPerMicrosecond;
            double sx = 0;
            double sy = 0;
            int steps = 0;
            for (long t = tick; _momentum.Step(t, out double dx, out double dy); t += tick)
            {
                sx += dx;
                sy += dy;
                steps++;
            }

            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"momentum v=({vx:F0}, {vy:F0}) pt/s coasts ({sx:F1}, {sy:F1}) pt in {steps} steps over {duration / 1000.0:F0}ms"));
        }
    }
}
