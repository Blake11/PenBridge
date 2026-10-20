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
    /// <summary>After a slider is released, how long older snapshot values are ignored while waiting for the reply.</summary>
    private const long ReleaseHoldMs = 1000;

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
        AddSlider(MacSettingKey.TipFeel, "Tip feel", FormatTipFeel);
        AddSlider(MacSettingKey.ClickThreshold, "Click threshold: below it the pen hovers", FormatPercent, ParsePercent);
        AddSlider(MacSettingKey.FullPressureAt, "Full pressure at", FormatPercent, ParsePercent);

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

    private LinearLayout AddSpeedSlider(MacSettingKey key, string title) => AddSlider(key, title, FormatSpeed);

    /// <summary>
    /// A slider for a Float key, shaped by Core's <see cref="SettingSliders"/> spec. Sends while dragging (throttled by
    /// <see cref="SliderField"/>) and on release; a typed value or a −/+ tap sends once. A snapshot doesn't move it while
    /// the finger is on it. The pressure pair is held valid here (<see cref="MacSettings.IsPressurePairValid"/>), so
    /// the slider stops where the Mac would reject the value.
    /// </summary>
    private LinearLayout AddSlider(MacSettingKey key, string title, Func<double, string> format, Func<string, double?>? parse = null)
    {
        if (!SettingSliders.TryGet(key, out SliderSpec spec))
        {
            throw new InvalidOperationException("No slider spec for " + key);
        }

        LinearLayout column = Ui.Column(_ctx);
        column.AddView(Ui.Text(_ctx, title), Ui.FullWidthLp(_ctx, 0));

        // After a release, snapshots that still carry an older (mid-drag) value are ignored until the reply with the
        // released value arrives or the hold times out; then the latest snapshot is applied.
        long holdUntil = 0;
        float heldValue = 0;
        SliderField? field = null;

        double Valid(double value)
        {
            if (_values is not { } v)
            {
                return value;
            }

            if (key == MacSettingKey.ClickThreshold)
            {
                while (value > spec.Min && !MacSettings.IsPressurePairValid((float)value, v.FullPressureAt))
                {
                    value = spec.StepDown(value);
                }
            }
            else if (key == MacSettingKey.FullPressureAt)
            {
                while (value < spec.Max && !MacSettings.IsPressurePairValid(v.ClickThreshold, (float)value))
                {
                    value = spec.StepUp(value);
                }
            }

            return value;
        }

        void Changed(double value, bool final)
        {
            if (_updating)
            {
                return;
            }

            if (final)
            {
                long now = SystemClock.UptimeMillis();
                heldValue = (float)value;
                holdUntil = now + ReleaseHoldMs;
                column.PostDelayed(
                    () =>
                    {
                        if (field is { Tracking: false } && holdUntil != 0 && SystemClock.UptimeMillis() >= holdUntil)
                        {
                            holdUntil = 0;
                            if (_values is { } latest)
                            {
                                field.SetValue(latest.Get(key).FloatValue);
                            }
                        }
                    },
                    ReleaseHoldMs);
            }

            Send(SettingEntry.Float(key, (float)value));
        }

        field = new SliderField(_ctx, spec, format, Changed, Valid, parse);
        _binders.Add(v =>
        {
            if (field.Tracking)
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

            field.SetValue(v.Get(key).FloatValue);
        });
        column.AddView(field.View, Ui.FullWidthLp(_ctx, 0));
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

    private static string FormatSpeed(double value) => value.ToString("0.00", CultureInfo.InvariantCulture) + "×";

    private static string FormatPercent(double value) =>
        Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) + " %";

    /// <summary>Typed percent ("5", "5 %") to the fraction the setting holds.</summary>
    private static double? ParsePercent(string text) => SliderField.ParseNumber(text) / 100;

    private static string FormatTipFeel(double value) =>
        Math.Abs(value) < 0.005 ? "0" : value.ToString("+0.00;−0.00", CultureInfo.InvariantCulture);
}
