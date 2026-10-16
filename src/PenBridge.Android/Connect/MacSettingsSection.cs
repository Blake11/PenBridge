using System.Globalization;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using PenBridge.Android.Services;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Android.Connect;

/// <summary>
/// The "Mac" section of the Settings screen (ADR-0030): the connected Mac's settings, edited remotely. Shown only
/// while <see cref="ClientHost.MacSettings"/> is available (a 1.2 Mac with RemoteSettings). Every change sends a
/// SettingsUpdate; the controls redraw from each snapshot, which is the truth (a rejected value springs back).
/// Ranges and value names come from Core's table (<see cref="MacSettings"/>). Drawing the region needs the Mac's
/// screen; only "Reset region to full display" is offered here. UI thread only.
/// </summary>
#pragma warning disable CA1001 // The views belong to the Settings page's hierarchy, which the screen owns and releases.
internal sealed class MacSettingsSection
#pragma warning restore CA1001
{
    /// <summary>While a slider is dragged, at most one update per this interval (the Mac caps updates at 20/s).</summary>
    private const long DragSendIntervalMs = 100;

    /// <summary>After a slider is released, how long older snapshot values are ignored while waiting for the reply.</summary>
    private const long ReleaseHoldMs = 1000;

    /// <summary>Speed sliders: log2 steps of a twentieth of an octave, so 1× sits in the middle of 0.25×..4×.</summary>
    private const double SpeedStepsPerOctave = 20;

    private readonly Context _ctx;
    private readonly ClientHost _host;
    private readonly LinearLayout _root;
    private readonly TextView _title;
    private readonly RadioGroup _targets;
    private readonly TextView _regionText;
    private readonly View _resetRegion;
    private readonly List<Action<MacSettingValues>> _binders = [];
    private IReadOnlyList<DisplayListEntry>? _builtDisplays;
    private MacSettingValues? _values;
    private bool _updating;

    public MacSettingsSection(Context context, ClientHost host)
    {
        _ctx = context;
        _host = host;
        _root = Ui.Column(context);
        _title = Ui.Text(context, "Mac", TextRole.Label);
        _root.AddView(_title);
        _root.AddView(Ui.Text(context, "Settings of the connected Mac. Changes apply on the Mac at once.", TextRole.Secondary));

        // Mapping (ADR-0031).
        _root.AddView(Ui.Text(context, "Target display", TextRole.Title), Ui.FullWidthLp(context, 12));
        _targets = new RadioGroup(context) { Orientation = global::Android.Widget.Orientation.Vertical };
        _targets.CheckedChange += (_, e) =>
        {
            if (!_updating && _targets.FindViewById(e.CheckedId) is RadioButton { Tag: Java.Lang.String id })
            {
                Send(SettingEntry.String(MacSettingKey.TargetDisplay, id.ToString()));
            }
        };
        _root.AddView(_targets, Ui.FullWidthLp(context, 0));
        _regionText = Ui.Text(context, null, TextRole.Secondary);
        _root.AddView(_regionText, Ui.FullWidthLp(context, 4));
        _resetRegion = Ui.TextButton(context, "Reset region to full display", () => Send(SettingEntry.Rect(MacSettingKey.Region, NormalizedRect.Full)));
        _root.AddView(_resetRegion, Ui.WrapLp());
        _binders.Add(BindRegion);

        // Pen (ADR-0032).
        _root.AddView(Ui.Text(context, "Pen", TextRole.Title), Ui.FullWidthLp(context, 16));
        AddChoices(MacSettingKey.StylusMode);
        View mouseSpeed = AddSpeedSlider(MacSettingKey.MouseSpeed, "Mouse speed");
        View acceleration = AddSwitch(MacSettingKey.MouseAcceleration, "Mouse acceleration");
        _binders.Add(v =>
        {
            bool mouse = v.StylusMode == 1;
            Ui.SetEnabled(mouseSpeed, mouse);
            Ui.SetEnabled(acceleration, mouse);
        });
        _root.AddView(Ui.Text(context, "Side button", TextRole.Title), Ui.FullWidthLp(context, 12));
        AddChoices(MacSettingKey.SideButton);
        _root.AddView(Ui.Text(context, "Pressure", TextRole.Title), Ui.FullWidthLp(context, 12));
        AddFloatSlider(MacSettingKey.TipFeel, "Tip feel", 0.05, FormatTipFeel);
        AddFloatSlider(MacSettingKey.ClickThreshold, "Click threshold: below it the pen hovers", 0.01, FormatPercent);
        AddFloatSlider(MacSettingKey.FullPressureAt, "Full pressure at", 0.01, FormatPercent);

        // Gestures (ADR-0033).
        _root.AddView(Ui.Text(context, "Gestures", TextRole.Title), Ui.FullWidthLp(context, 16));
        _root.AddView(Ui.Text(context, "Smart Zoom (two-finger double tap)"), Ui.FullWidthLp(context, 4));
        AddChoices(MacSettingKey.SmartZoom);
        AddSpeedSlider(MacSettingKey.PointerSpeed, "Pointer speed");
        AddSpeedSlider(MacSettingKey.ScrollSpeed, "Scroll speed");
        AddSwitch(MacSettingKey.Zoom, "Pinch to zoom");
        AddSwitch(MacSettingKey.Rotate, "Rotate");
        AddSwitch(MacSettingKey.ThreeFingerDrag, "Three-finger drag");
        AddSwitch(MacSettingKey.FourFingerGestures, "Four-finger gestures");
        _root.Visibility = ViewStates.Gone;
    }

    public View View => _root;

    /// <summary>Redraws from the host's latest snapshot and display list; hides the section without them.</summary>
    public void Refresh()
    {
        MacSettingValues? values = _host.MacSettings;
        _values = values;
        if (values is null)
        {
            _root.Visibility = ViewStates.Gone;
            return;
        }

        _updating = true;
        try
        {
            _title.Text = "Mac: " + (_host.State.MacName ?? "connected");
            IReadOnlyList<DisplayListEntry> displays = _host.MacDisplays;
            if (!ReferenceEquals(displays, _builtDisplays))
            {
                BuildTargets(displays);
            }

            CheckTarget(values.TargetDisplay, displays);
            foreach (Action<MacSettingValues> bind in _binders)
            {
                bind(values);
            }
        }
        finally
        {
            _updating = false;
        }

        _root.Visibility = ViewStates.Visible;
    }

    private void Send(SettingEntry entry)
    {
        if (!_updating && _values is not null)
        {
            _host.UpdateMacSettings([entry]);
        }
    }

    private void BuildTargets(IReadOnlyList<DisplayListEntry> displays)
    {
        _builtDisplays = displays;
        _targets.RemoveAllViews();
        foreach (DisplayListEntry display in displays)
        {
            string name = PeerText.Sanitize(display.Name);
            if (name.Length == 0)
            {
                name = "Display";
            }

            // The main display's choice is "follow the main display" (the default), not its id: the user can always return to it.
            AddTargetChoice(display.IsMain ? name + " (main)" : name, display.IsMain ? MacSettings.MainDisplay : display.Id);
        }

        AddTargetChoice("All displays", MacSettings.AllDisplays);
    }

    private void AddTargetChoice(string label, string id)
    {
        RadioButton button = Choice(label);
        button.Tag = new Java.Lang.String(id);
        _targets.AddView(button);
    }

    /// <summary>
    /// Checks the target's button. The "(main)" button stands for "" (follow the main display); a target set to the
    /// main display's own id (e.g. on the Mac) checks it too.
    /// </summary>
    private void CheckTarget(string target, IReadOnlyList<DisplayListEntry> displays)
    {
        foreach (DisplayListEntry display in displays)
        {
            if (display.IsMain && display.Id == target)
            {
                target = MacSettings.MainDisplay;
                break;
            }
        }

        for (int i = 0; i < _targets.ChildCount; i++)
        {
            if (_targets.GetChildAt(i) is RadioButton { Tag: Java.Lang.String id } button && id.ToString() == target)
            {
                if (_targets.CheckedRadioButtonId != button.Id)
                {
                    _targets.Check(button.Id);
                }

                return;
            }
        }

        _targets.ClearCheck(); // an id the list doesn't have (yet)
    }

    private void BindRegion(MacSettingValues values)
    {
        bool all = values.TargetDisplay == MacSettings.AllDisplays;
        bool full = IsFull(values.Region);
        _regionText.Text = all
            ? "Regions don't apply to all displays."
            : full ? "Region: the whole display. Set a region in the Mac's PenBridge settings."
            : "Region: part of the display (set on the Mac).";
        Ui.SetEnabled(_resetRegion, !all && !full);
    }

    private static bool IsFull(NormalizedRect r) =>
        Math.Abs(r.X) < 1e-4 && Math.Abs(r.Y) < 1e-4 && Math.Abs(r.Width - 1) < 1e-4 && Math.Abs(r.Height - 1) < 1e-4;

    /// <summary>Radio buttons for an Int key, one per value name of the table.</summary>
    private void AddChoices(MacSettingKey key)
    {
        MacSettingDescriptor d = MacSettings.Get(key);
        var group = new RadioGroup(_ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
        var buttons = new RadioButton[d.Values.Count];
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i] = Choice(ValueLabel(d.Values[i]));
            buttons[i].Tag = Java.Lang.Integer.ValueOf(i);
            group.AddView(buttons[i]);
        }

        group.CheckedChange += (_, e) =>
        {
            if (!_updating && group.FindViewById(e.CheckedId) is RadioButton { Tag: Java.Lang.Integer value })
            {
                Send(SettingEntry.Int(key, value.IntValue()));
            }
        };
        _binders.Add(v =>
        {
            int value = v.Get(key).IntValue;
            if (value >= 0 && value < buttons.Length && group.CheckedRadioButtonId != buttons[value].Id)
            {
                group.Check(buttons[value].Id);
            }
        });
        _root.AddView(group, Ui.FullWidthLp(_ctx, 0));
    }

    private Switch AddSwitch(MacSettingKey key, string text)
    {
        var toggle = new Switch(_ctx) { Text = text };
        toggle.SetTextColor(Ui.ColorOf(_ctx, Resource.Color.pb_on_surface));
        toggle.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        toggle.SetMinHeight(Ui.Dp(_ctx, 48));
        toggle.CheckedChange += (_, e) => Send(SettingEntry.Bool(key, e.IsChecked));
        _binders.Add(v => toggle.Checked = v.Get(key).BoolValue);
        _root.AddView(toggle, Ui.FullWidthLp(_ctx, 4));
        return toggle;
    }

    /// <summary>0.25×..4× on a log scale.</summary>
    private LinearLayout AddSpeedSlider(MacSettingKey key, string title)
    {
        MacSettingDescriptor d = MacSettings.Get(key);
        double lo = Math.Log2(d.Min);
        int steps = (int)Math.Round((Math.Log2(d.Max) - lo) * SpeedStepsPerOctave);
        return AddSlider(
            key,
            title,
            steps,
            step => Math.Clamp((float)Math.Pow(2, lo + (step / SpeedStepsPerOctave)), d.Min, d.Max),
            value => (int)Math.Round((Math.Log2(value) - lo) * SpeedStepsPerOctave),
            static value => value.ToString("0.00", CultureInfo.InvariantCulture) + "×");
    }

    /// <summary>A linear slider over the key's range in steps of <paramref name="step"/>.</summary>
    private LinearLayout AddFloatSlider(MacSettingKey key, string title, double step, Func<float, string> format)
    {
        MacSettingDescriptor d = MacSettings.Get(key);
        int steps = (int)Math.Round((d.Max - d.Min) / step);
        return AddSlider(
            key,
            title,
            steps,
            s => Math.Clamp((float)(d.Min + (s * step)), d.Min, d.Max),
            value => (int)Math.Round((value - d.Min) / step),
            format);
    }

    /// <summary>
    /// A slider for a Float key. Sends while dragging (throttled) and on release; a snapshot doesn't move it while
    /// the finger is on it. The pressure pair is held valid here (<see cref="MacSettings.IsPressurePairValid"/>), so
    /// the slider stops where the Mac would reject the value.
    /// </summary>
    private LinearLayout AddSlider(MacSettingKey key, string title, int steps, Func<int, float> toValue, Func<float, int> toStep, Func<float, string> format)
    {
        LinearLayout column = Ui.Column(_ctx);
        column.AddView(Ui.Text(_ctx, title), Ui.FullWidthLp(_ctx, 0));
        LinearLayout row = Ui.Row(_ctx);
        var seek = new SeekBar(_ctx) { Max = steps, Min = 0 };
        TextView label = Ui.Text(_ctx, null, TextRole.Title);
        bool tracking = false;
        long lastSent = 0;

        // After a release, snapshots that still carry an older (mid-drag) value are ignored until the reply with the
        // released value arrives or the hold times out; then the latest snapshot is applied.
        long holdUntil = 0;
        float heldValue = 0;

        void Apply(MacSettingValues v)
        {
            float value = v.Get(key).FloatValue;
            seek.Progress = Math.Clamp(toStep(value), 0, steps);
            label.Text = format(value);
        }

        int Valid(int step)
        {
            if (_values is not { } v)
            {
                return step;
            }

            if (key == MacSettingKey.ClickThreshold)
            {
                while (step > 0 && !MacSettings.IsPressurePairValid(toValue(step), v.FullPressureAt))
                {
                    step--;
                }
            }
            else if (key == MacSettingKey.FullPressureAt)
            {
                while (step < steps && !MacSettings.IsPressurePairValid(v.ClickThreshold, toValue(step)))
                {
                    step++;
                }
            }

            return step;
        }

        void SendStep(int step)
        {
            lastSent = SystemClock.UptimeMillis();
            float value = toValue(step);
            if (!tracking)
            {
                heldValue = value;
                holdUntil = lastSent + ReleaseHoldMs;
                seek.PostDelayed(
                    () =>
                    {
                        if (!tracking && holdUntil != 0 && SystemClock.UptimeMillis() >= holdUntil)
                        {
                            holdUntil = 0;
                            if (_values is { } latest)
                            {
                                Apply(latest);
                            }
                        }
                    },
                    ReleaseHoldMs);
            }

            Send(SettingEntry.Float(key, value));
        }

        seek.ProgressChanged += (_, e) =>
        {
            int step = e.Progress;
            if (e.FromUser && !_updating)
            {
                int valid = Valid(step);
                if (valid != step)
                {
                    seek.Progress = valid; // re-enters with FromUser false
                    return;
                }

                if (!tracking || SystemClock.UptimeMillis() - lastSent >= DragSendIntervalMs)
                {
                    SendStep(step);
                }
            }

            label.Text = format(toValue(step));
        };
        seek.StartTrackingTouch += (_, _) => tracking = true;
        seek.StopTrackingTouch += (_, _) =>
        {
            tracking = false;
            SendStep(seek.Progress);
        };
        _binders.Add(v =>
        {
            if (tracking)
            {
                return;
            }

            if (holdUntil != 0)
            {
                if (v.Get(key).FloatValue != heldValue && SystemClock.UptimeMillis() < holdUntil)
                {
                    return; // a reply to an update sent before the release
                }

                holdUntil = 0;
            }

            Apply(v);
        });
        row.AddView(seek, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(label, new LinearLayout.LayoutParams(Ui.Dp(_ctx, 96), ViewGroup.LayoutParams.WrapContent));
        column.AddView(row, Ui.FullWidthLp(_ctx, 0));
        _root.AddView(column, Ui.FullWidthLp(_ctx, 8));
        return column;
    }

    private RadioButton Choice(string label)
    {
        var button = new RadioButton(_ctx) { Text = label, Id = View.GenerateViewId() };
        button.SetTextColor(Ui.ColorOf(_ctx, Resource.Color.pb_on_surface));
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        button.SetMinHeight(Ui.Dp(_ctx, 48));
        return button;
    }

    /// <summary>Display text for a value name of the table; unknown names show as they are.</summary>
    private static string ValueLabel(string name) => name switch
    {
        "Pen" => "Pen (absolute)",
        "Mouse" => "Mouse (relative, moves the cursor from where it is)",
        "RightClick" => "Right click",
        "MiddleClick" => "Middle click",
        "Eraser" => "Eraser while held",
        "DoubleClick" => "Double click",
        "None" => "Nothing",
        "FollowSystem" => "Follow macOS",
        _ => name,
    };

    private static string FormatPercent(float value) =>
        Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) + " %";

    private static string FormatTipFeel(float value) =>
        Math.Abs(value) < 0.005f ? "Linear"
        : value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + (value < 0 ? " soft" : " firm");
}
