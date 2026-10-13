using System.Globalization;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using PenBridge.Android.Services;
using PenBridge.Core;

namespace PenBridge.Android.Connect;

/// <summary>
/// Advanced ▸ Connect manually (ADR-0008): host/IP, port and the PIN shown in the Mac's pairing window, for networks
/// that block mDNS (and the emulator, where the Mac is 10.0.2.2). A host:port this phone is already paired with
/// connects without a PIN. The same pairing and pinning apply; nothing is relaxed.
/// </summary>
internal sealed class ManualConnectScreen : AttemptScreen
{
    private EditText? _host;
    private EditText? _port;
    private EditText? _pin;
    private Button? _connect;
    private TextView? _error;
    private TextView? _progress;

    public ManualConnectScreen(MainActivity activity)
        : base(activity)
    {
    }

    protected override View CreateView()
    {
        var ctx = Activity;
        ScrollView page = Ui.Page(ctx, out LinearLayout content);
        content.AddView(Ui.Header(ctx, "Connect manually", () => Activity.Screens.Pop()));
        content.AddView(Ui.Text(
            ctx,
            "Use this when your Mac doesn't show up in the list. The Mac's “Pair new device…” window shows its address, port and PIN."));

        _error = Ui.ErrorBanner(ctx);
        content.AddView(_error, Ui.FullWidthLp(ctx, 16));

        content.AddView(Ui.Text(ctx, "Address", TextRole.Label));
        _host = Ui.Field(ctx, "e.g. 192.168.1.20", InputTypes.ClassText | InputTypes.TextVariationUri);
        content.AddView(_host, Ui.FullWidthLp(ctx, 0));

        content.AddView(Ui.Text(ctx, "Port", TextRole.Label));
        _port = Ui.Field(ctx, ProtocolInfo.DefaultPort.ToString(CultureInfo.InvariantCulture), InputTypes.ClassNumber, maxLength: 5);
        _port.Text = ProtocolInfo.DefaultPort.ToString(CultureInfo.InvariantCulture);
        content.AddView(_port, Ui.FullWidthLp(ctx, 0));

        content.AddView(Ui.Text(ctx, "PIN (not needed if already paired)", TextRole.Label));
        _pin = Ui.PinField(ctx);
        _pin.EditorAction += (_, e) =>
        {
            e.Handled = e.ActionId == ImeAction.Done;
            if (e.Handled)
            {
                Start();
            }
        };
        content.AddView(_pin, Ui.FullWidthLp(ctx, 0));

        if (Host.Settings.LastMac is { } last)
        {
            _host.Text = last.Host;
            _port.Text = last.Port.ToString(CultureInfo.InvariantCulture);
        }

        _host.TextChanged += (_, _) => Render(Host.State);
        _port.TextChanged += (_, _) => Render(Host.State);
        _pin.TextChanged += (_, _) => Render(Host.State);

        _connect = Ui.FilledButton(ctx, "Connect", Start);
        content.AddView(_connect, Ui.FullWidthLp(ctx, 24));

        _progress = Ui.Text(ctx, null, TextRole.Secondary);
        _progress.Gravity = GravityFlags.CenterHorizontal;
        content.AddView(_progress, Ui.FullWidthLp(ctx, 16));
        return page;
    }

    protected override void Render(ClientHostState state)
    {
        if (_connect is null)
        {
            return;
        }

        bool busy = IsBusy;
        Ui.ShowBanner(_error!, ErrorFor(state));
        _progress!.Text = state.Status == ClientHostStatus.Pairing ? "Pairing…"
            : state.Status == ClientHostStatus.Connecting ? "Connecting…"
            : null;
        _host!.Enabled = _port!.Enabled = _pin!.Enabled = !busy;
        bool valid = TryRead(out string host, out int port, out string pin)
            && (pin.Length == 6 || FindPaired(host, port) is not null);
        Ui.SetEnabled(_connect, !busy && valid);
    }

    private bool TryRead(out string host, out int port, out string pin)
    {
        host = (_host?.Text ?? string.Empty).Trim();
        pin = (_pin?.Text ?? string.Empty).Trim();
        bool portOk = int.TryParse((_port?.Text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= ushort.MaxValue;
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1]; // IPv6 literal typed in URL form
        }

        return host.Length > 0 && portOk && (pin.Length == 0 || pin.Length == 6);
    }

    private KnownMac? FindPaired(string host, int port)
    {
        foreach (KnownMac mac in Host.Settings.GetKnownMacs())
        {
            if (mac.Port == port && string.Equals(mac.Host, host, StringComparison.OrdinalIgnoreCase) && Host.IsPaired(mac.Fingerprint))
            {
                return mac;
            }
        }

        return null;
    }

    private void Start()
    {
        if (IsBusy || !TryRead(out string host, out int port, out string pin))
        {
            return;
        }

        Ui.HideKeyboard(_pin!);
        if (pin.Length == 6)
        {
            MarkAttempt();
            _ = Host.PairWithPinAsync(host, port, pin);
        }
        else if (FindPaired(host, port) is { } known)
        {
            MarkAttempt();
            _ = Host.ConnectAsync(known);
        }
    }
}
