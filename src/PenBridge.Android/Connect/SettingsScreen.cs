using System.Globalization;
using Android.Content.PM;
using Android.Views;
using Android.Widget;
using PenBridge.Android.Services;
using PenBridge.Core;

namespace PenBridge.Android.Connect;

/// <summary>
/// About + settings: finger drawing pressure, default pad orientation, and the paired Macs (forget removes this
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
        var readout = new Switch(ctx) { Text = "Show touch size on the pad (for tuning)", Checked = settings.ShowTouchSize };
        readout.SetTextColor(Ui.ColorOf(ctx, Resource.Color.pb_on_surface));
        readout.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        readout.SetMinHeight(Ui.Dp(ctx, 48));
        readout.CheckedChange += (_, e) => settings.ShowTouchSize = e.IsChecked;
        content.AddView(readout, Ui.FullWidthLp(ctx, 8));

        // Default orientation of the pad (also changeable from the pad's menu).
        content.AddView(Ui.Text(ctx, "Pad orientation", TextRole.Label));
        var group = new RadioGroup(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
        AddChoice(group, "Landscape (locked)", OrientationMode.LockLandscape, settings.Orientation);
        AddChoice(group, "Portrait (locked)", OrientationMode.LockPortrait, settings.Orientation);
        AddChoice(group, "Auto-rotate (between strokes)", OrientationMode.Auto, settings.Orientation);
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

    private void AddChoice(RadioGroup group, string label, OrientationMode mode, OrientationMode current)
    {
        var ctx = Activity;
        var button = new RadioButton(ctx)
        {
            Text = label,
            Id = View.GenerateViewId(),
            Tag = Java.Lang.Integer.ValueOf((int)mode),
        };
        button.SetTextColor(Ui.ColorOf(ctx, Resource.Color.pb_on_surface));
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        button.SetMinHeight(Ui.Dp(ctx, 48));
        group.AddView(button);
        if (mode == current)
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
                Host.Forget(mac.Fingerprint);
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
