using System.Globalization;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using PenBridge.Core.Settings;

namespace PenBridge.Android.Connect;

/// <summary>
/// A settings slider (#212): the track (snaps at the default through <see cref="SliderSpec.FromPosition"/>), a value
/// field (numeric keyboard; Done or focus-out commits through <see cref="SliderSpec.Coerce"/>, invalid text reverts)
/// and − / + buttons. While dragging, <c>changed</c> runs at most once per <see cref="DragSendIntervalMs"/> and once on
/// release; a typed value or a button tap runs it once. UI thread only.
/// </summary>
#pragma warning disable CA1001 // The views belong to the page's hierarchy, which the screen owns and releases.
internal sealed class SliderField
#pragma warning restore CA1001
{
    /// <summary>While a slider is dragged, at most one update per this interval (the Mac caps updates at 20/s).</summary>
    public const long DragSendIntervalMs = 100;

    /// <summary>Track resolution; fine enough that a pixel is below any step.</summary>
    private const int TrackSteps = 1000;

    /// <summary>Longer text is not a number worth parsing (also bounds the stack buffer).</summary>
    private const int MaxParseLength = 32;

    private readonly SliderSpec _spec;
    private readonly Func<double, string> _format;
    private readonly Action<double, bool> _changed;
    private readonly Func<double, double>? _constrain;
    private readonly Func<string, double?> _parse;
    private readonly SeekBar _seek;
    private readonly EditText _field;
    private double _value;
    private long _lastSent;
    private string _shownText = string.Empty;

    /// <param name="spec">Range, step, default and scale.</param>
    /// <param name="format">Display text of a value, shown in the field.</param>
    /// <param name="changed">User changes: the value, and whether it is final (release, typed value or button; false while dragging).</param>
    /// <param name="constrain">Optional extra rule applied to every user value (the pressure pair); must return a value on the spec's grid.</param>
    /// <param name="parse">Optional text to value; defaults to <see cref="ParseNumber"/>.</param>
    public SliderField(
        Context context,
        SliderSpec spec,
        Func<double, string> format,
        Action<double, bool> changed,
        Func<double, double>? constrain = null,
        Func<string, double?>? parse = null)
    {
        _spec = spec;
        _format = format;
        _changed = changed;
        _constrain = constrain;
        _parse = parse ?? ParseNumber;

        View = Ui.Column(context);
        _seek = new SeekBar(context) { Max = TrackSteps, Min = 0 };
        ((LinearLayout)View).AddView(_seek, Ui.FullWidthLp(context, 0));

        LinearLayout controls = Ui.Row(context);
        controls.SetGravity(GravityFlags.CenterVertical | GravityFlags.End);
        Button minus = StepButton(context, "−", "Decrease");
        Button plus = StepButton(context, "+", "Increase");
        _field = Ui.Field(context, string.Empty, InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned);
        _field.ImeOptions = ImeAction.Done;
        _field.Gravity = GravityFlags.Center;
        _field.SetMinWidth(Ui.Dp(context, 96));
        _field.SetSelectAllOnFocus(true);
        int pad = Ui.Dp(context, 8);
        _field.SetPadding(pad, pad, pad, pad);
        _field.SetMinHeight(Ui.Dp(context, 48));
        var fieldLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        fieldLp.SetMargins(Ui.Dp(context, 8), 0, Ui.Dp(context, 8), 0);
        controls.AddView(minus, Ui.WrapLp());
        controls.AddView(_field, fieldLp);
        controls.AddView(plus, Ui.WrapLp());
        ((LinearLayout)View).AddView(controls, Ui.FullWidthLp(context, 0));

        minus.Click += (_, _) => Commit(ConstrainedStep(down: true));
        plus.Click += (_, _) => Commit(ConstrainedStep(down: false));
        _field.EditorAction += (_, e) =>
        {
            if (e.ActionId == ImeAction.Done)
            {
                CommitText();
                _field.ClearFocus();
                Ui.HideKeyboard(_field);
                e.Handled = true;
            }
        };
        _field.FocusChange += (_, e) =>
        {
            if (!e.HasFocus)
            {
                CommitText();
            }
        };

        _seek.ProgressChanged += (_, e) =>
        {
            if (!e.FromUser)
            {
                return;
            }

            double value = _spec.FromPosition(e.Progress / (double)TrackSteps);
            if (_constrain is not null)
            {
                double constrained = _constrain(value);
                if (constrained != value)
                {
                    value = constrained;
                    _seek.Progress = ToProgress(value); // programmatic: does not re-enter
                }
            }

            _value = value;
            ShowValue();
            long now = global::Android.OS.SystemClock.UptimeMillis();
            if (now - _lastSent >= DragSendIntervalMs)
            {
                _lastSent = now;
                _changed(value, false);
            }
        };
        _seek.StartTrackingTouch += (_, _) =>
        {
            Tracking = true;
            _lastSent = 0;
        };
        _seek.StopTrackingTouch += (_, _) =>
        {
            Tracking = false;
            _changed(_value, true);
        };

        SetValue(spec.Default);
    }

    public View View { get; }

    /// <summary>True while the finger is on the track.</summary>
    public bool Tracking { get; private set; }

    /// <summary>The shown value.</summary>
    public double Value => _value;

    /// <summary>Shows a value from outside (a snapshot or the store) without raising <c>changed</c>.</summary>
    public void SetValue(double value)
    {
        _value = _spec.Coerce(value);
        _seek.Progress = ToProgress(_value);
        ShowValue();
    }

    /// <summary>Greys out the whole row.</summary>
    public void SetEnabled(bool enabled) => Ui.SetEnabled(View, enabled);

    /// <summary>Extracts the number from text such as "1.00×", "5 %", "−0.20" or "0,5"; null when there is none.</summary>
    public static double? ParseNumber(string text)
    {
        if (text.Length > MaxParseLength)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[text.Length];
        int n = 0;
        foreach (char c in text)
        {
            if (c is >= '0' and <= '9' or '.' or '+' or '-')
            {
                buffer[n++] = c;
            }
            else if (c is '−' or '–')
            {
                buffer[n++] = '-';
            }
            else if (c == ',')
            {
                buffer[n++] = '.';
            }
        }

        return double.TryParse(buffer[..n], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
    }

    private double ConstrainedStep(bool down)
    {
        double next = down ? _spec.StepDown(_value) : _spec.StepUp(_value);
        return _constrain?.Invoke(next) ?? next;
    }

    private int ToProgress(double value) => (int)Math.Round(_spec.ToPosition(value) * TrackSteps);

    private void ShowValue()
    {
        if (!_field.HasFocus)
        {
            ShowText();
        }
    }

    private void ShowText()
    {
        _shownText = _format(_value);
        _field.Text = _shownText;
    }

    private void CommitText()
    {
        string text = _field.Text ?? string.Empty;
        if (text == _shownText)
        {
            ShowText(); // untouched: never resend, and catch up with a value that changed while focused
            return;
        }

        double? typed = _parse(text);
        if (typed is null)
        {
            ShowText(); // invalid: revert
            return;
        }

        double value = _spec.Coerce(typed.Value);
        if (_constrain is not null)
        {
            value = _constrain(value);
        }

        Commit(value);
    }

    private void Commit(double value)
    {
        bool same = value == _value;
        _value = value;
        _seek.Progress = ToProgress(value);
        ShowText();
        if (!same)
        {
            _changed(value, true);
        }
    }

    private static Button StepButton(Context context, string text, string description)
    {
        Button button = Ui.TonalButton(context, text, static () => { });
        button.ContentDescription = description;
        button.SetMinWidth(Ui.Dp(context, 48));
        button.SetPadding(Ui.Dp(context, 12), 0, Ui.Dp(context, 12), 0);
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20);
        return button;
    }
}
