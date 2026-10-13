using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}

internal sealed class RecordingSink : IInputSink
{
    public List<PenEvent> Pen { get; } = new(4096);

    public List<GestureEvent> Gestures { get; } = new(4096);

    public void OnPenEvent(in PenEvent e) => Pen.Add(e);

    public void OnGestureEvent(in GestureEvent e) => Gestures.Add(e);

    public PenEventKind[] PenKinds() => Pen.Select(e => e.Kind).ToArray();

    public void Clear()
    {
        Pen.Clear();
        Gestures.Clear();
    }
}

internal static class Samples
{
    public static PenSample Hover(ushort x, ushort y, PenTool tool = PenTool.Pen, PenButtons buttons = PenButtons.None) =>
        new(x, y, 0, 0, 0, 0, tool, PenState.InRange, buttons);

    public static PenSample Touch(ushort x, ushort y, ushort pressure = 30000, PenTool tool = PenTool.Pen, PenButtons buttons = PenButtons.None) =>
        new(x, y, pressure, 0, 0, 0, tool, PenState.InRange | PenState.Contact, buttons);
}
