using AppKit;
using CoreGraphics;
using Foundation;
using PenBridge.Core.Input;

namespace PenBridge.Mac.Settings;

/// <summary>
/// A pad that draws strokes from the window's own mouse and tablet events, stroke width by
/// <see cref="NSEvent.Pressure"/>: the pressure an app receives (with PenBridge, after the Mac's curve). A plain
/// mouse reports 1 while pressed.
/// </summary>
/// <remarks>Event-driven, no timers. Keeps at most <see cref="MaxSamples"/> samples (oldest strokes dropped).</remarks>
internal sealed class PressureTestPad : NSView
{
    private const int MaxSamples = 8192;
    private const double MaxWidth = 14;

    // One list of samples; a NaN pressure separates strokes.
    private readonly List<(CGPoint Point, double Pressure)> _samples = [];

    public PressureTestPad()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AccessibilityRole = NSAccessibilityRoles.ImageRole;
        AccessibilityLabel = "Pressure test pad";
    }

    /// <summary>Raised with each sample's pressure (0..1), and with NaN when the stroke ends.</summary>
    public event EventHandler<double>? PressureSampled;

    public override bool IsFlipped => true;

    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override bool MouseDownCanMoveWindow => false;

    public void Clear()
    {
        _samples.Clear();
        NeedsDisplay = true;
    }

    public override void MouseDown(NSEvent theEvent)
    {
        if (_samples.Count > 0)
        {
            _samples.Add((default, double.NaN));
        }

        Add(theEvent);
    }

    public override void MouseDragged(NSEvent theEvent) => Add(theEvent);

    public override void MouseUp(NSEvent theEvent) => PressureSampled?.Invoke(this, double.NaN);

    public override void DrawRect(CGRect dirtyRect)
    {
        NSColor.TextBackground.SetFill();
        NSGraphics.RectFill(Bounds);

        NSColor.Label.SetStroke();
        for (int i = 1; i < _samples.Count; i++)
        {
            (CGPoint a, double pa) = _samples[i - 1];
            (CGPoint b, double pb) = _samples[i];
            if (double.IsNaN(pa) || double.IsNaN(pb))
            {
                continue;
            }

            double width = Math.Max(0.5, (pa + pb) / 2 * MaxWidth);
            using NSBezierPath path = new();
            path.LineWidth = (nfloat)width;
            path.LineCapStyle = NSLineCapStyle.Round;
            path.MoveTo(a);
            path.LineTo(b);
            path.Stroke();
        }

        NSColor.Separator.SetStroke();
        using NSBezierPath border = NSBezierPath.FromRect(Bounds.Inset(0.5f, 0.5f));
        border.LineWidth = 1;
        border.Stroke();
    }

    private void Add(NSEvent e)
    {
        double pressure = Math.Clamp((double)e.Pressure, 0, 1);
        if (_samples.Count >= MaxSamples)
        {
            _samples.RemoveRange(0, MaxSamples / 2);
        }

        _samples.Add((ConvertPointFromView(e.LocationInWindow, null), pressure));
        NeedsDisplay = true;
        PressureSampled?.Invoke(this, pressure);
    }
}

/// <summary>Graph of the pressure curve for the current (possibly in-drag) Pen settings: input across, output up.</summary>
internal sealed class PressureCurveView : NSView
{
    private const int Steps = 48;
    private PressureCurve _curve = PressureCurve.Linear;

    public PressureCurveView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AccessibilityRole = NSAccessibilityRoles.ImageRole;
        AccessibilityLabel = "Pressure curve";
    }

    public void SetCurve(double tipFeel, double clickThreshold, double fullPressureAt)
    {
        // A slider mid-drag can momentarily break threshold < full; Core rejects that, so keep the last valid curve.
        try
        {
            _curve = new PressureCurve(tipFeel, clickThreshold, fullPressureAt);
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        NeedsDisplay = true;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        CGRect box = Bounds.Inset(1, 1);
        NSColor.TextBackground.SetFill();
        NSGraphics.RectFill(Bounds);

        // Linear reference.
        NSColor.TertiaryLabel.SetStroke();
        using (NSBezierPath diagonal = new())
        {
            diagonal.LineWidth = 1;
            diagonal.MoveTo(new CGPoint(box.GetMinX(), box.GetMinY()));
            diagonal.LineTo(new CGPoint(box.GetMaxX(), box.GetMaxY()));
            diagonal.Stroke();
        }

        NSColor.ControlAccent.SetStroke();
        using (NSBezierPath curve = new())
        {
            curve.LineWidth = 2;
            for (int i = 0; i <= Steps; i++)
            {
                double x = (double)i / Steps;
                double y = _curve.Apply(x);
                var point = new CGPoint(box.GetMinX() + (x * box.Width), box.GetMinY() + (y * box.Height));
                if (i == 0)
                {
                    curve.MoveTo(point);
                }
                else
                {
                    curve.LineTo(point);
                }
            }

            curve.Stroke();
        }

        NSColor.Separator.SetStroke();
        using NSBezierPath border = NSBezierPath.FromRect(Bounds.Inset(0.5f, 0.5f));
        border.LineWidth = 1;
        border.Stroke();
    }
}
