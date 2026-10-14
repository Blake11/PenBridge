using Android.Content;
using Android.Graphics;
using Android.Views;
using PenBridge.Core.Input;

namespace PenBridge.Android.Pad;

/// <summary>
/// Debug overlay for tuning the palm threshold (#76): the live normalized contact size (<see cref="RawTouch.Size"/>,
/// what the filter compares with <see cref="InputFilterOptions.PalmSizeThreshold"/>) of each finger, and the peak of
/// the last touch. Turns amber while a contact counts as a palm. Draws only, never takes input. Allocation-free: sizes
/// are copied into fixed buffers and formatted into a reused char buffer at draw time. UI thread only.
/// </summary>
internal sealed class TouchSizeReadoutView : View
{
    private const int MaxShown = 10;
    private static readonly Color Normal = Color.Argb(0xB0, 0xC3, 0xC6, 0xCF);
    private static readonly Color Palm = Color.Argb(0xE0, 0xF0, 0xAA, 0x28);

    private readonly float[] _sizes = new float[MaxShown];
    private readonly char[] _text = new char[16 + (MaxShown * 6) + 32];
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private readonly float _threshold;
    private readonly int _pad;
    private readonly float _ascent;
    private readonly float _lineHeight;
    private int _count;
    private float _peak;
    private bool _touching;

    public TouchSizeReadoutView(Context context, float palmThreshold)
        : base(context)
    {
        _threshold = palmThreshold;
        float density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        _paint.TextSize = 13f * density;
        _paint.SetTypeface(Typeface.Monospace);
        _pad = (int)(8f * density);
        using (Paint.FontMetrics? fm = _paint.GetFontMetrics())
        {
            _ascent = fm?.Ascent ?? -_paint.TextSize;
            _lineHeight = fm is null ? _paint.TextSize * 1.2f : fm.Descent - fm.Ascent;
        }

        Clickable = false;
        Focusable = false;
        ImportantForAccessibility = ImportantForAccessibility.No;
    }

    /// <summary>Copies the current fingers' sizes (count 0 = all lifted; the peak is kept until the next touch).</summary>
    public void Update(ReadOnlySpan<RawTouch> fingers, int count)
    {
        int n = Math.Min(count, MaxShown);
        if (n > 0 && !_touching)
        {
            _peak = 0f; // a new touch starts a new peak
        }

        _touching = n > 0;
        for (int i = 0; i < n; i++)
        {
            float size = fingers[i].Size;
            _sizes[i] = size;
            _peak = Math.Max(_peak, size);
        }

        _count = n;
        Invalidate();
    }

    public override bool OnTouchEvent(MotionEvent? e) => false;

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = (int)_paint.MeasureText("size 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00 0.00  peak 0.00  palm>=0.00") + (2 * _pad);
        SetMeasuredDimension(ResolveSize(width, widthMeasureSpec), ResolveSize((int)_lineHeight + (2 * _pad), heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        int len = 0;
        len = Append(_text, len, "size");
        bool palm = false;
        for (int i = 0; i < _count; i++)
        {
            len = Append(_text, len, " ");
            len = AppendSize(_text, len, _sizes[i]);
            palm |= _sizes[i] > 0f && _sizes[i] >= _threshold;
        }

        if (_count == 0)
        {
            len = Append(_text, len, " -");
        }

        len = Append(_text, len, "  peak ");
        len = AppendSize(_text, len, _peak);
        len = Append(_text, len, _threshold > 1f ? "  palm off" : "  palm>=");
        if (_threshold <= 1f)
        {
            len = AppendSize(_text, len, _threshold);
        }

        _paint.Color = palm ? Palm : Normal;
        canvas.DrawText(_text, 0, len, _pad, _pad - _ascent, _paint);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _paint.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int Append(char[] buffer, int at, string text)
    {
        foreach (char c in text)
        {
            if (at < buffer.Length)
            {
                buffer[at++] = c;
            }
        }

        return at;
    }

    /// <summary>Writes a 0..1 size as "0.00" (two decimals) without allocating.</summary>
    private static int AppendSize(char[] buffer, int at, float size)
    {
        int hundredths = (int)MathF.Round(Math.Clamp(size, 0f, 1f) * 100f);
        if (at + 4 > buffer.Length)
        {
            return at;
        }

        buffer[at++] = hundredths >= 100 ? '1' : '0';
        buffer[at++] = '.';
        int rest = hundredths >= 100 ? 0 : hundredths;
        buffer[at++] = (char)('0' + (rest / 10));
        buffer[at++] = (char)('0' + (rest % 10));
        return at;
    }
}
