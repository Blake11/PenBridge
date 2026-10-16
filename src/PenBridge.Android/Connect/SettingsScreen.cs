using System.Globalization;
using Android.Content.PM;
using Android.Views;
using Android.Widget;
using PenBridge.Android.Pad;
using PenBridge.Android.Services;
using PenBridge.Core;
using PenBridge.Core.Input;

namespace PenBridge.Android.Connect;

/// <summary>
/// About + settings: the pad surface look (ADR-0031), finger mode and drawing pressure, default pad orientation, and
/// the paired Macs (forget removes this
/// phone's key for that Mac; the Mac keeps its own record until it's removed there too).
/// </summary>
internal sealed class SettingsScreen : Screen
{
    private const int MinPressurePercent = 5;
    private const long NoFlags = 0;
    private const int ArmStepMs = 10;

    /// <summary>Palm slider in hundredths: 5..100 is the threshold, this last step means off.</summary>
    private const int PalmOffStep = 101;

    private LinearLayout? _paired;
    private TextView? _pressureValue;

    public SettingsScreen(MainActivity activity)
        : base(activity)
    {
    }

    public override void Resume() => RebuildPaired();

    protected override View CreateView()
    {
        var ctx = Activity;
        SettingsStore settings = Host.Settings;
        ScrollView page = Ui.Page(ctx, out LinearLayout content);
        content.AddView(Ui.Header(ctx, "Settings", () => Activity.Screens.Pop()));

        // Surface look (ADR-0031, phone-only): applied when the pad is shown again.
        content.AddView(Ui.Text(ctx, "Surface", TextRole.Label));
        content.AddView(Ui.Text(ctx, "Background"));
        var backgroundGroup = new RadioGroup(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
        AddChoice(backgroundGroup, "Black", (int)SurfaceBackground.Black, (int)settings.SurfaceBackground);
        AddChoice(backgroundGroup, "Dark grey", (int)SurfaceBackground.DarkGrey, (int)settings.SurfaceBackground);
        AddChoice(backgroundGroup, "Light", (int)SurfaceBackground.Light, (int)settings.SurfaceBackground);
        backgroundGroup.CheckedChange += (_, e) =>
        {
            if (backgroundGroup.FindViewById(e.CheckedId) is RadioButton { Tag: Java.Lang.Integer value })
            {
                settings.SurfaceBackground = (SurfaceBackground)value.IntValue();
            }
        };
        content.AddView(backgroundGroup, Ui.FullWidthLp(ctx, 0));
        content.AddView(Ui.Text(ctx, "Marker opacity: the corner ticks and the centre cross."), Ui.FullWidthLp(ctx, 8));
        content.AddView(SliderRow(
            (int)MathF.Round(SurfaceLook.MinMarkerOpacity * 100),
            100,
            (int)MathF.Round(settings.MarkerOpacity * 100),
            FormatPercent,
            step => settings.MarkerOpacity = step / 100f), Ui.FullWidthLp(ctx, 8));
        content.AddView(SwitchRow("Centre marker", settings.CentreMarker, on => settings.CentreMarker = on), Ui.FullWidthLp(ctx, 8));
#if DEBUG
        content.AddView(
            Ui.TextButton(ctx, "Preview pad (debug)", () => Activity.Screens.Push(new PadScreen(Activity, preview: true))),
            Ui.WrapLp());
#endif

        // Finger mode (ADR-0022): what one finger does when no stylus is present.
        content.AddView(Ui.Text(ctx, "One finger", TextRole.Label));
        var fingerGroup = new RadioGroup(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
        AddChoice(fingerGroup, "Cursor (tap to click, double-tap and hold to drag)", (int)FingerFallbackMode.Cursor, (int)settings.FingerMode);
        AddChoice(fingerGroup, "Draw", (int)FingerFallbackMode.Draw, (int)settings.FingerMode);
        fingerGroup.CheckedChange += (_, e) =>
        {
            if (fingerGroup.FindViewById(e.CheckedId) is RadioButton { Tag: Java.Lang.Integer mode })
            {
                settings.FingerMode = (FingerFallbackMode)mode.IntValue();
            }
        };
        content.AddView(fingerGroup, Ui.FullWidthLp(ctx, 0));

        // Finger pressure (used when no stylus is present and a single finger draws).
        content.AddView(Ui.Text(ctx, "Finger drawing", TextRole.Label));
        content.AddView(Ui.Text(ctx, "Pressure used when you draw with a finger instead of the S Pen."));
        LinearLayout pressureRow = Ui.Row(ctx);
        var seek = new SeekBar(ctx) { Max = 100, Min = MinPressurePercent };
        seek.Progress = ToPercent(settings.FingerPressure);
        _pressureValue = Ui.Text(ctx, FormatPercent(seek.Progress), TextRole.Title);
        seek.ProgressChanged += (_, e) =>
        {
            _pressureValue.Text = FormatPercent(e.Progress);
            if (e.FromUser)
            {
                settings.FingerPressure = FromPercent(e.Progress);
            }
        };
        pressureRow.AddView(seek, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        pressureRow.AddView(_pressureValue, new LinearLayout.LayoutParams(Ui.Dp(ctx, 56), ViewGroup.LayoutParams.WrapContent));
        content.AddView(pressureRow, Ui.FullWidthLp(ctx, 8));

        // Finger and palm filtering (InputFilter): tune on the phone with the touch-size readout.
        content.AddView(Ui.Text(ctx, "Fingers and palm", TextRole.Label));
        content.AddView(Ui.Text(ctx, "Finger hold before drawing: a second finger in this time starts a gesture instead of a stroke."));
        content.AddView(SliderRow(
            0,
            SettingsStore.MaxFingerArmDelayMs / ArmStepMs,
            settings.FingerArmDelayMs / ArmStepMs,
            static step => (step * ArmStepMs).ToString(CultureInfo.InvariantCulture) + " ms",
            step => settings.FingerArmDelayMs = step * ArmStepMs), Ui.FullWidthLp(ctx, 8));
        content.AddView(Ui.Text(ctx, "Palm size: a contact at least this large (0 to 1 of the sensor range) is ignored as a palm. Higher lets bigger fingers through."), Ui.FullWidthLp(ctx, 12));
        content.AddView(SliderRow(
            (int)(SettingsStore.MinPalmSizeThreshold * 100),
            PalmOffStep,
            settings.PalmSizeThreshold > 1f ? PalmOffStep : (int)MathF.Round(settings.PalmSizeThreshold * 100),
            static step => step >= PalmOffStep ? "Off" : (step / 100.0).ToString("0.00", CultureInfo.InvariantCulture),
            step => settings.PalmSizeThreshold = step >= PalmOffStep ? SettingsStore.PalmSizeThresholdOff : step / 100f), Ui.FullWidthLp(ctx, 8));
        content.AddView(
            SwitchRow("Show touch size on the pad (for tuning)", settings.ShowTouchSize, on => settings.ShowTouchSize = on),
            Ui.FullWidthLp(ctx, 8));

        // Default orientation of the pad (also changeable from the pad's menu).
        content.AddView(Ui.Text(ctx, "Pad orientation", TextRole.Label));
        var group = new RadioGroup(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
        AddChoice(group, "Landscape (locked)", (int)OrientationMode.LockLandscape, (int)settings.Orientation);
        AddChoice(group, "Portrait (locked)", (int)OrientationMode.LockPortrait, (int)settings.Orientation);
        AddChoice(group, "Auto-rotate (between strokes)", (int)OrientationMode.Auto, (int)settings.Orientation);
        group.CheckedChange += (_, e) =>
        {
            if (group.FindViewById(e.CheckedId) is RadioButton { Tag: Java.Lang.Integer mode })
            {
                settings.Orientation = (OrientationMode)mode.IntValue();
            }
        };
        content.AddView(group, Ui.FullWidthLp(ctx, 0));

        content.AddView(Ui.Text(ctx, "Paired Macs", TextRole.Label));
        _paired = Ui.Column(ctx);
        content.AddView(_paired, Ui.FullWidthLp(ctx, 0));

        content.AddView(Ui.Text(ctx, "About", TextRole.Label));
        content.AddView(Ui.Text(
            ctx,
            "PenBridge " + AppVersion() + " · protocol " + ProtocolInfo.Version.ToString()
                + "\nTurns this phone into a pressure-sensitive drawing tablet for your Mac. Input is encrypted and only paired Macs receive it."));
        return page;
    }

    /// <summary>A slider with a value label; <paramref name="changed"/> runs on user changes only.</summary>
    private LinearLayout SliderRow(int min, int max, int value, Func<int, string> format, Action<int> changed)
    {
        var ctx = Activity;
        LinearLayout row = Ui.Row(ctx);
        var seek = new SeekBar(ctx) { Max = max, Min = min };
        seek.Progress = Math.Clamp(value, min, max);
        TextView label = Ui.Text(ctx, format(seek.Progress), TextRole.Title);
        seek.ProgressChanged += (_, e) =>
        {
            label.Text = format(e.Progress);
            if (e.FromUser)
            {
                changed(e.Progress);
            }
        };
        row.AddView(seek, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(label, new LinearLayout.LayoutParams(Ui.Dp(ctx, 72), ViewGroup.LayoutParams.WrapContent));
        return row;
    }

    /// <summary>A labelled switch; <paramref name="changed"/> runs on every toggle.</summary>
    private Switch SwitchRow(string text, bool on, Action<bool> changed)
    {
        var ctx = Activity;
        var toggle = new Switch(ctx) { Text = text, Checked = on };
        toggle.SetTextColor(Ui.ColorOf(ctx, Resource.Color.pb_on_surface));
        toggle.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        toggle.SetMinHeight(Ui.Dp(ctx, 48));
        toggle.CheckedChange += (_, e) => changed(e.IsChecked);
        return toggle;
    }

    /// <summary>Adds a radio choice tagged with <paramref name="value"/>; checked when it equals <paramref name="current"/>.</summary>
    private void AddChoice(RadioGroup group, string label, int value, int current)
    {
        var ctx = Activity;
        var button = new RadioButton(ctx)
        {
            Text = label,
            Id = View.GenerateViewId(),
            Tag = Java.Lang.Integer.ValueOf(value),
        };
        button.SetTextColor(Ui.ColorOf(ctx, Resource.Color.pb_on_surface));
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        button.SetMinHeight(Ui.Dp(ctx, 48));
        group.AddView(button);
        if (value == current)
        {
            group.Check(button.Id);
        }
    }

    private void RebuildPaired()
    {
        if (_paired is null)
        {
            return;
        }

        var ctx = Activity;
        _paired.RemoveAllViews();
        foreach (KnownMac mac in Host.Settings.GetKnownMacs())
        {
            if (!Host.IsPaired(mac.Fingerprint))
            {
                continue;
            }

            LinearLayout row = Ui.Row(ctx);
            row.SetBackgroundResource(Resource.Drawable.pb_bg_card);
            row.SetPadding(Ui.Dp(ctx, 16), Ui.Dp(ctx, 8), Ui.Dp(ctx, 8), Ui.Dp(ctx, 8));
            LinearLayout text = Ui.Column(ctx);
            text.AddView(Ui.Text(ctx, mac.Name, TextRole.Title));
            text.AddView(Ui.Text(ctx, SettingsStore.FormatEndpoint(mac.Host, mac.Port), TextRole.Secondary));
            row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            KnownMac target = mac;
            row.AddView(Ui.TextButton(ctx, "Forget", () => ConfirmForget(target)), Ui.WrapLp());
            _paired.AddView(row, Ui.FullWidthLp(ctx, _paired.ChildCount == 0 ? 0 : 8));
        }

        if (_paired.ChildCount == 0)
        {
            _paired.AddView(Ui.Text(ctx, "No paired Macs."));
        }
    }

    private void ConfirmForget(KnownMac mac)
    {
        new AlertDialog.Builder(Activity)
            .SetTitle("Forget " + mac.Name + "?")!
            .SetMessage("This phone deletes its key for this Mac and has to pair again to use it. Also remove the phone from the Mac's paired devices.")!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .SetPositiveButton("Forget", (_, _) =>
            {
                bool current = Host.CurrentMac is { } now && now.Id == mac.Id;
                Host.Forget(mac.Fingerprint);
                if (current && Activity.Screens.HasSessionPad)
                {
                    // Opened over the pad: that session just ended for good, so leave the dead pad for the Mac list.
                    Activity.Screens.PopToRoot();
                    return;
                }

                RebuildPaired();
            })!
            .Show();
    }

    private string AppVersion()
    {
        try
        {
            PackageManager? pm = Activity.PackageManager;
            string package = Activity.PackageName ?? string.Empty;
            PackageInfo? info = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? pm?.GetPackageInfo(package, PackageManager.PackageInfoFlags.Of(NoFlags))
#pragma warning disable CS0618, CA1422 // API 29-32 only have the int-flags overload.
                : pm?.GetPackageInfo(package, 0);
#pragma warning restore CS0618, CA1422
            return info?.VersionName ?? "?";
        }
        catch (PackageManager.NameNotFoundException)
        {
            return "?";
        }
    }

    private static int ToPercent(ushort pressure) => Math.Clamp((int)Math.Round(pressure * 100.0 / ushort.MaxValue), MinPressurePercent, 100);

    private static ushort FromPercent(int percent) => (ushort)Math.Clamp((int)Math.Round(percent * ushort.MaxValue / 100.0), 1, ushort.MaxValue);

    private static string FormatPercent(int percent) => percent.ToString(CultureInfo.InvariantCulture) + " %";
}
