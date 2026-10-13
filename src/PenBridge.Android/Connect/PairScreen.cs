using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using PenBridge.Android.Services;
using PenBridge.Core.Net;

namespace PenBridge.Android.Connect;

/// <summary>
/// Pairing (ADR-0009): with a discovered Mac, enter the 6-digit PIN it shows, or scan its QR code. "Pair new Mac"
/// (no Mac picked) offers the QR code only, since a PIN needs an address. The activity opens the pad on success.
/// </summary>
internal sealed class PairScreen : AttemptScreen
{
    private readonly DiscoveredServer? _mac;
    private EditText? _pin;
    private Button? _pair;
    private Button? _scan;
    private TextView? _error;
    private TextView? _progress;

    public PairScreen(MainActivity activity, DiscoveredServer? mac)
        : base(activity)
    {
        _mac = mac;
    }

    protected override View CreateView()
    {
        var ctx = Activity;
        ScrollView page = Ui.Page(ctx, out LinearLayout content);
        content.AddView(Ui.Header(ctx, _mac is null ? "Pair new Mac" : "Pair with " + _mac.Name, () => Activity.Screens.Pop()));

        string intro = _mac is null
            ? "On the Mac, open PenBridge in the menu bar and choose “Pair new device…”, then scan the QR code it shows."
                + " To use the PIN instead, pick the Mac in the list, or use Advanced ▸ Connect manually."
            : "On the Mac, open PenBridge in the menu bar and choose “Pair new device…”. Enter the 6-digit PIN it shows, or scan its QR code.";
        content.AddView(Ui.Text(ctx, intro), Ui.FullWidthLp(ctx, 4));

        _error = Ui.ErrorBanner(ctx);
        content.AddView(_error, Ui.FullWidthLp(ctx, 16));

        if (_mac is not null)
        {
            content.AddView(Ui.Text(ctx, "PIN", TextRole.Label));
            _pin = Ui.PinField(ctx);
            _pin.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 24);
            _pin.LetterSpacing = 0.3f;
            _pin.TextChanged += (_, _) => Render(Host.State);
            _pin.EditorAction += (_, e) =>
            {
                e.Handled = e.ActionId == ImeAction.Done;
                if (e.Handled)
                {
                    StartPin();
                }
            };
            content.AddView(_pin, Ui.FullWidthLp(ctx, 0));

            _pair = Ui.FilledButton(ctx, "Pair", StartPin);
            content.AddView(_pair, Ui.FullWidthLp(ctx, 16));
        }

        _scan = _mac is null ? Ui.FilledButton(ctx, "Scan QR code", OnScan) : Ui.TonalButton(ctx, "Scan QR code instead", OnScan);
        content.AddView(_scan, Ui.FullWidthLp(ctx, _mac is null ? 24 : 12));

        _progress = Ui.Text(ctx, null, TextRole.Secondary);
        _progress.Gravity = GravityFlags.CenterHorizontal;
        content.AddView(_progress, Ui.FullWidthLp(ctx, 16));
        return page;
    }

    protected override void Render(ClientHostState state)
    {
        if (_scan is null)
        {
            return;
        }

        bool busy = IsBusy;
        Ui.ShowBanner(_error!, ErrorFor(state));
        _progress!.Text = state.Status == ClientHostStatus.Pairing ? "Pairing…"
            : state.Status == ClientHostStatus.Connecting ? "Connecting…"
            : null;
        SetEnabled(_scan, !busy);
        if (_pin is not null)
        {
            _pin.Enabled = !busy;
            SetEnabled(_pair!, !busy && PinText().Length == 6);
        }
    }

    private string PinText() => _pin?.Text?.Trim() ?? string.Empty;

    private void StartPin()
    {
        string pin = PinText();
        if (_mac is null || IsBusy || pin.Length != 6)
        {
            return;
        }

        Ui.HideKeyboard(_pin!);
        MarkAttempt();
        _ = Host.PairWithPinAsync(_mac, pin);
    }

    private void OnScan()
    {
        if (!IsBusy)
        {
            Activity.OpenQrScanner();
        }
    }

    private static void SetEnabled(Button button, bool enabled) => Ui.SetEnabled(button, enabled);
}
