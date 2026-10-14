using Android.Views;
using Android.Widget;
using PenBridge.Android.Services;
using PenBridge.Core.Net;

namespace PenBridge.Android.Connect;

/// <summary>
/// Home screen: Macs found on the network and Macs paired before, plus Pair new Mac, Advanced ▸ Connect manually and
/// Settings. Tapping a paired Mac connects (the activity opens the pad once connected); an unpaired one opens PIN/QR
/// pairing. Shows the host's progress and any error (as local text, ErrorText).
/// </summary>
internal sealed class MacListScreen : Screen
{
    private LinearLayout? _list;
    private TextView? _status;
    private ProgressBar? _spinner;
    private TextView? _error;
    private LinearLayout? _progressRow;
    private TextView? _progressText;

    public MacListScreen(MainActivity activity)
        : base(activity)
    {
    }

    /// <summary>While set, the startup auto-connect window is open (shows "Looking for …").</summary>
    public string? LookingFor { get; set; }

    public override void Resume()
    {
        Render(Host.State);
        RebuildList();
    }

    public override void OnHostStateChanged(ClientHostState state) => Render(state);

    public override void OnDiscoveredChanged()
    {
        RebuildList();
        Render(Host.State);
    }

    /// <summary>Re-renders after the startup window changed.</summary>
    public void Refresh() => Render(Host.State);

    protected override View CreateView()
    {
        var ctx = Activity;
        ScrollView page = Ui.Page(ctx, out LinearLayout content);
        content.AddView(Ui.Header(ctx, "PenBridge", back: null, "Settings", () => Activity.Screens.Push(new SettingsScreen(Activity))));

        LinearLayout statusRow = Ui.Row(ctx);
        _spinner = new ProgressBar(ctx) { Indeterminate = true };
        statusRow.AddView(_spinner, new LinearLayout.LayoutParams(Ui.Dp(ctx, 18), Ui.Dp(ctx, 18)) { RightMargin = Ui.Dp(ctx, 10) });
        _status = Ui.Text(ctx, null, TextRole.Secondary);
        statusRow.AddView(_status, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        content.AddView(statusRow, Ui.FullWidthLp(ctx, 4));

        _progressRow = Ui.Row(ctx);
        _progressRow.SetBackgroundResource(Resource.Drawable.pb_bg_tonal);
        int pad = Ui.Dp(ctx, 14);
        _progressRow.SetPadding(pad, Ui.Dp(ctx, 6), Ui.Dp(ctx, 6), Ui.Dp(ctx, 6));
        _progressText = Ui.Text(ctx, null, TextRole.Body);
        _progressText.SetTextColor(Ui.ColorOf(ctx, Resource.Color.pb_on_primary_container));
        _progressRow.AddView(_progressText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _progressRow.AddView(Ui.TextButton(ctx, "Cancel", OnCancel), Ui.WrapLp());
        _progressRow.Visibility = ViewStates.Gone;
        content.AddView(_progressRow, Ui.FullWidthLp(ctx));

        _error = Ui.ErrorBanner(ctx);
        content.AddView(_error, Ui.FullWidthLp(ctx));

        content.AddView(Ui.Text(ctx, "Macs", TextRole.Label));
        _list = Ui.Column(ctx);
        content.AddView(_list, Ui.FullWidthLp(ctx, 0));

        content.AddView(Ui.FilledButton(ctx, "Pair new Mac", () => Activity.Screens.Push(new PairScreen(Activity, mac: null))), Ui.FullWidthLp(ctx, 24));
        Button manual = Ui.TextButton(ctx, "Advanced ▸ Connect manually", () => Activity.Screens.Push(new ManualConnectScreen(Activity)));
        content.AddView(manual, Ui.FullWidthLp(ctx, 8));
        return page;
    }

    private void Render(ClientHostState state)
    {
        if (_status is null)
        {
            return;
        }

        MacDiscovery discovery = Host.Discovery;
        bool busy = state.Status is ClientHostStatus.Connecting or ClientHostStatus.Pairing;
        if (discovery.LastFailure is not null && !discovery.IsBrowsing)
        {
            _status.Text = "Can't search this network for Macs. Use Advanced ▸ Connect manually.";
            _spinner!.Visibility = ViewStates.Gone;
        }
        else
        {
            _status.Text = discovery.IsBrowsing ? "Searching this Wi-Fi for Macs running PenBridge…" : "Not searching.";
            _spinner!.Visibility = discovery.IsBrowsing ? ViewStates.Visible : ViewStates.Gone;
        }

        if (busy)
        {
            string name = state.MacName ?? "the Mac";
            _progressText!.Text = state.Status == ClientHostStatus.Pairing ? "Pairing with " + name + "…"
                : state.IsReconnecting ? "Reconnecting to " + name + "…"
                : "Connecting to " + name + "…";
            _progressRow!.Visibility = ViewStates.Visible;
        }
        else if (LookingFor is { } looking)
        {
            _progressText!.Text = "Looking for " + looking + "…";
            _progressRow!.Visibility = ViewStates.Visible;
        }
        else
        {
            _progressRow!.Visibility = ViewStates.Gone;
        }

        Ui.ShowBanner(_error!, state.Status == ClientHostStatus.Error ? ErrorText.Describe(state) : null);
    }

    private void RebuildList()
    {
        if (_list is null)
        {
            return;
        }

        var ctx = Activity;
        _list.RemoveAllViews();
        IReadOnlyList<DiscoveredServer> found = Host.DiscoveredMacs;
        IReadOnlyList<KnownMac> known = Host.Settings.GetKnownMacs();

        foreach (DiscoveredServer mac in found)
        {
            bool paired = Host.IsPaired(mac);
            string subtitle = !mac.IsCompatible ? "Needs a different PenBridge version"
                : paired ? "Paired · tap to connect"
                : "Not paired · tap to pair";
            Action? tap = !mac.IsCompatible ? null : paired ? () => Connect(mac) : () => Activity.Screens.Push(new PairScreen(Activity, mac));
            AddRow(Ui.Card(ctx, mac.Name, subtitle, tap, out _));
        }

        foreach (KnownMac mac in known)
        {
            if (IsDiscovered(found, mac) || !Host.IsPaired(mac.Fingerprint))
            {
                continue;
            }

            string subtitle = "Paired · not found nearby · tap to try " + SettingsStore.FormatEndpoint(mac.Host, mac.Port);
            AddRow(Ui.Card(ctx, mac.Name, subtitle, () => Connect(mac), out _));
        }

        if (_list.ChildCount == 0)
        {
            _list.AddView(Ui.Text(
                ctx,
                "No Macs yet. Open PenBridge on your Mac and keep both devices on the same Wi-Fi, or tap Pair new Mac and scan the QR code it shows."));
        }
    }

    private void AddRow(View row) => _list!.AddView(row, Ui.FullWidthLp(Activity, _list.ChildCount == 0 ? 0 : 8));

    private static bool IsDiscovered(IReadOnlyList<DiscoveredServer> found, KnownMac mac)
    {
        foreach (DiscoveredServer server in found)
        {
            if (server.HasFingerprint(mac.Fingerprint))
            {
                return true;
            }
        }

        return false;
    }

    private void Connect(DiscoveredServer mac)
    {
        if (!IsBusy())
        {
            _ = Host.ConnectAsync(mac);
        }
    }

    private void Connect(KnownMac mac)
    {
        if (!IsBusy())
        {
            _ = Host.ConnectAsync(mac);
        }
    }

    private bool IsBusy() => Host.State.Status is ClientHostStatus.Connecting or ClientHostStatus.Pairing;

    private void OnCancel()
    {
        Activity.EndStartupWindow();
        if (IsBusy())
        {
            Host.Disconnect();
        }
    }
}
