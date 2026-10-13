using AppKit;
using CoreAnimation;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using PenBridge.Core.Net;
using PenBridge.Mac.Services;

namespace PenBridge.Mac.Pairing;

/// <summary>
/// "Pair new device…": a floating panel with the QR code, the PIN, the host IP(s):port for manual entry (ADR-0008),
/// a countdown and a status line. Showing it opens the server's pairing window; closing it closes that window.
/// </summary>
/// <remarks>
/// <para><b>Threading:</b> main thread only (like <see cref="ServerHost"/>, whose events arrive on it).</para>
/// <para><b>Timers:</b> one 1 s repeating <see cref="NSTimer"/> while the panel is visible (countdown, lockout wait,
/// failed-attempt count, lazy expiry, server restart), replaced by a one-shot 1.5 s timer after a successful pairing; none once closed.
/// The panel is created on <see cref="Show"/> and released on close, so a never-opened window costs nothing.</para>
/// <para><b>Semantics</b> (<see cref="PenBridge.Core.Security.PinIssuer"/>): after 3 failures the issuer replaces the
/// PIN and starts a backoff, shown as "Locked: try again in N s" with the new PIN; on expiry the window closes, so the
/// panel shows "Expired…" and opens a new one on the next tick. Success closes the issuer's window first and raises
/// <see cref="ServerHost.DevicePaired"/> after the Keychain write, so in between the panel shows "Confirming…".</para>
/// <para>Never log the PIN or the QR text.</para>
/// </remarks>
public sealed class PairingWindowController : IDisposable
{
    private const double QrPoints = 220;
    private const double QrPadding = 12;
    private const double ContentWidth = 340;
    private const double SuccessSeconds = 1.5;

    // Pairing confirmed by the issuer but no DevicePaired yet (e.g. the registry write failed): give up and re-issue.
    private const int ConfirmGraceTicks = 30;

    private readonly ServerHost _host;
    private NSPanel? _panel;
    private NSView? _qrBox;
    private NSView? _qrView;
    private NSImageView? _symbol;
    private NSTextField? _manual;
    private NSLayoutConstraint? _qrBoxSize;
    private NSLayoutConstraint? _qrViewSize;
    private NSTextField? _pin;
    private NSTextField? _endpoints;
    private NSTextField? _countdown;
    private NSTextField? _status;
    private NSTimer? _timer;
    private string? _qrText;
    private double _qrPoints;
    private Phase _phase;
    private DateTimeOffset _expiresAt;
    private int _confirmTicks;
    private bool _disposed;

    public PairingWindowController(ServerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>Raised after the panel closed (close button, <see cref="Close"/>, or after a successful pairing).</summary>
    public event EventHandler? Closed;

    private enum Phase
    {
        /// <summary>The issuer's window is open; PIN/QR are shown.</summary>
        Open,

        /// <summary>The window expired; a new one opens on the next tick.</summary>
        Expired,

        /// <summary>The issuer closed the window before its expiry: a pairing succeeded, DevicePaired is on its way.</summary>
        Confirming,

        /// <summary>The server isn't listening; reopen when <see cref="ServerHost.StateChanged"/> says it is.</summary>
        ServerDown,

        /// <summary>Showing "Paired with …" until the panel closes itself.</summary>
        Paired,
    }

    /// <summary>Whether the panel is on screen.</summary>
    public bool IsVisible => _panel is not null;

    /// <summary>
    /// Shows the panel, activates the app (an LSUIElement app has to, to take focus) and opens the pairing window with a
    /// fresh PIN. If it is already visible, only brings it to the front (the PIN stays).
    /// </summary>
    public void Show()
    {
        EnsureMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_panel is null)
        {
            _panel = CreatePanel();
            _host.PairingChanged += OnPairingChanged;
            _host.StateChanged += OnStateChanged;
            _host.DevicePaired += OnDevicePaired;
            Reopen();
            _panel.Center();
            _timer = NSTimer.CreateRepeatingScheduledTimer(1, _ => Tick());
            _timer.Tolerance = 0.1;
        }

        // Activation is cooperative since macOS 14 and may be refused (e.g. not launched from a click), so also order
        // the floating panel front regardless.
        NSApplication.SharedApplication.Activate();
        _panel.MakeKeyAndOrderFront(null);
        _panel.OrderFrontRegardless();
    }

    /// <summary>Closes the panel and the pairing window (the PIN and QR token become invalid). No-op when hidden.</summary>
    public void Close()
    {
        EnsureMainThread();
        _panel?.Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _disposed = true;
    }

    private void OnWillClose(object? sender, EventArgs e)
    {
        NSPanel? panel = _panel;
        if (panel is null)
        {
            return;
        }

        _panel = null;
        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = null;
        _host.PairingChanged -= OnPairingChanged;
        _host.StateChanged -= OnStateChanged;
        _host.DevicePaired -= OnDevicePaired;
        _host.ClosePairingWindow();

        panel.WillClose -= OnWillClose;
        _qrBox = _qrView = null;
        _symbol = null;
        _manual = null;
        _qrBoxSize = _qrViewSize = null;
        _pin = _endpoints = _countdown = _status = null;
        _qrText = null;
        _qrPoints = 0;

        // AppKit is still inside -close; drop our reference to the panel afterwards.
        DispatchQueue.MainQueue.DispatchAsync(panel.Dispose);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPairingChanged(object? sender, EventArgs e) => Refresh();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_phase == Phase.ServerDown)
        {
            if (IsServerRunning())
            {
                Reopen();
            }
        }
        else if (!IsServerRunning())
        {
            // Stopped (sleep, quit, failure): its PairingChanged came before the status, so re-evaluate now.
            Refresh();
        }
    }

    private void OnDevicePaired(object? sender, DevicePairedEventArgs e)
    {
        if (_panel is null || _phase == Phase.Paired)
        {
            return;
        }

        _phase = Phase.Paired;
        ShowSecrets(null);
        SetSymbol("checkmark.circle.fill", NSColor.SystemGreen, "Paired");
        SetDetailsVisible(false);
        _countdown!.AlphaValue = 0;
        SetStatus(PairingText.Paired(PeerText.Sanitize(e.Device.Name)), announce: true);
        FitToContent();

        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = NSTimer.CreateScheduledTimer(SuccessSeconds, _ => Close());
    }

    private void Tick()
    {
        switch (_phase)
        {
            case Phase.Expired:
                Reopen();
                break;
            case Phase.Confirming when ++_confirmTicks >= ConfirmGraceTicks:
                Reopen();
                break;
            case Phase.ServerDown:
                // StateChanged reopens it; this covers a status that read "running" before the new server existed.
                if (IsServerRunning())
                {
                    Reopen();
                }

                break;
            case Phase.Paired:
                break;
            default:
                // Wrong-PIN attempts raise no event, and expiry is only noticed when the issuer is read: pull every second.
                Refresh();
                break;
        }
    }

    private void Reopen()
    {
        _expiresAt = default;
        _confirmTicks = 0;
        _phase = _host.OpenPairingWindow() ? Phase.Open : Phase.ServerDown;
        Refresh();
    }

    private void Refresh()
    {
        if (_panel is null || _phase == Phase.Paired)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        PairingInfo? info = _host.GetPairingInfo();
        if (info is null)
        {
            ShowClosedWindow(now);
            return;
        }

        _phase = Phase.Open;
        _confirmTicks = 0;
        SetDetailsVisible(true);
        _expiresAt = info.ExpiresAt;
        ShowSecrets(info);
        _countdown!.StringValue = PairingText.Countdown(info.ExpiresAt - now);
        _countdown.AlphaValue = 1;
        string status = PairingText.Status(info, now);
        SetStatus(status, announce: !ReferenceEquals(status, PairingText.Waiting));
    }

    private void ShowClosedWindow(DateTimeOffset now)
    {
        ShowSecrets(null);
        _countdown!.AlphaValue = 0;
        if (_phase == Phase.ServerDown || !IsServerRunning())
        {
            // ServerDown here means OpenPairingWindow just failed; reopening is left to StateChanged and Tick.
            _phase = Phase.ServerDown;
            SetDetailsVisible(false);
            SetSymbol("exclamationmark.triangle", NSColor.SecondaryLabel, "Server not running");
            SetStatus(PairingText.ServerDown, announce: true);
        }
        else if (_phase == Phase.Expired || (_expiresAt != default && now >= _expiresAt))
        {
            _phase = Phase.Expired;
            SetSymbol("hourglass", NSColor.SecondaryLabel, "Expired");
            SetStatus(PairingText.Expired, announce: true);
        }
        else
        {
            _phase = Phase.Confirming;
            SetSymbol("hourglass", NSColor.SecondaryLabel, "Confirming");
            SetStatus(PairingText.Confirming, announce: false);
        }

        FitToContent();
    }

    private bool IsServerRunning() => _host.State.Status is ServerHostStatus.Listening or ServerHostStatus.Connected;

    /// <summary>Shows the PIN, QR and endpoints of <paramref name="info"/>, or blanks them (no longer valid).</summary>
    private void ShowSecrets(PairingInfo? info)
    {
        string? qrText = info?.Qr?.Format();
        bool resized = false;
        if (!string.Equals(qrText, _qrText, StringComparison.Ordinal))
        {
            _qrText = qrText;
            resized = SetQrImage(qrText);
        }

        if (info is null)
        {
            _pin!.StringValue = "––– –––";
            _pin.TextColor = NSColor.TertiaryLabel;
            _pin.AccessibilityLabel = "No PIN";
            return;
        }

        string grouped = PairingText.GroupPin(info.Pin);
        if (!string.Equals(_pin!.StringValue, grouped, StringComparison.Ordinal))
        {
            _pin.StringValue = grouped;
            _pin.TextColor = NSColor.Label;
            _pin.AccessibilityLabel = PairingText.SpokenPin(info.Pin);
        }

        string endpoints = PairingText.Endpoints(info.Addresses, info.Port);
        if (!string.Equals(_endpoints!.StringValue, endpoints, StringComparison.Ordinal))
        {
            _endpoints.StringValue = endpoints;
            resized = true;
        }

        if (resized)
        {
            FitToContent();
        }
    }

    /// <summary>Renders the QR (or clears it); true if its size changed.</summary>
    private bool SetQrImage(string? text)
    {
        CALayer layer = _qrView!.Layer!;
        if (text is null)
        {
            layer.Contents = null;
            _qrBox!.Layer!.BackgroundColor = new CGColor(1, 1, 1, 0);
            _qrBox.AccessibilityElement = false;
            return false;
        }

        _symbol!.Hidden = true;
        _qrBox!.Layer!.BackgroundColor = new CGColor(1, 1, 1, 1);
        _qrBox.AccessibilityElement = true;

        using CGImage? image = QrCodeRenderer.Render(text, QrPoints, out double points);
        layer.Contents = image;
        _qrBox.AccessibilityLabel = "Pairing QR code. Scan it with the PenBridge app on your phone.";
        if (image is null || points == _qrPoints)
        {
            return false;
        }

        _qrPoints = points;
        _qrViewSize!.Constant = (nfloat)points;
        _qrBoxSize!.Constant = (nfloat)(points + (2 * QrPadding));
        return true;
    }

    /// <summary>Shows an SF Symbol where the QR code was (the QR is already cleared).</summary>
    private void SetSymbol(string name, NSColor tint, string description)
    {
        NSImageView symbol = _symbol!;
        symbol.Image = NSImage.GetSystemSymbol(name, description);
        symbol.ContentTintColor = tint;
        symbol.Hidden = symbol.Image is null;
    }

    private void SetDetailsVisible(bool visible)
    {
        // Alpha, not Hidden: the stack view would detach hidden views and the panel would jump.
        foreach (NSView view in (ReadOnlySpan<NSView>)[_pin!, _manual!, _endpoints!])
        {
            view.AlphaValue = visible ? 1 : 0;
            view.AccessibilityHidden = !visible;
        }
    }

    private void SetStatus(string text, bool announce)
    {
        NSTextField status = _status!;
        if (string.Equals(status.StringValue, text, StringComparison.Ordinal))
        {
            return;
        }

        status.StringValue = text;
        if (announce)
        {
            using var info = new NSDictionary(
                NSAccessibilityNotificationUserInfoKeys.AnnouncementKey,
                new NSString(text),
                NSAccessibilityNotificationUserInfoKeys.PriorityKey,
                NSNumber.FromNInt((nint)(long)NSAccessibilityPriorityLevel.High));
            NSAccessibility.PostNotification(NSApplication.SharedApplication, NSView.AnnouncementRequestedNotification, info);
        }
    }

    private void FitToContent()
    {
        NSPanel? panel = _panel;
        if (panel?.ContentView is not { } content)
        {
            return;
        }

        content.LayoutSubtreeIfNeeded();
        CGRect frame = panel.Frame;
        CGRect next = panel.FrameRectFor(new CGRect(CGPoint.Empty, content.FittingSize));
        if (next.Size == frame.Size)
        {
            return;
        }

        // Keep the title bar where it is.
        next.X = frame.X;
        next.Y = frame.GetMaxY() - next.Height;
        panel.SetFrame(next, display: true);
    }

    private NSPanel CreatePanel()
    {
        var panel = new NSPanel(
            new CGRect(0, 0, ContentWidth, 480),
            NSWindowStyle.Titled | NSWindowStyle.Closable,
            NSBackingStore.Buffered,
            deferCreation: true)
        {
            Title = "Pair New Device",
            Level = NSWindowLevel.Floating,
            FloatingPanel = true,
            HidesOnDeactivate = false,
            BecomesKeyOnlyIfNeeded = false,
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.FullScreenAuxiliary,
        };
        HideButton(panel, NSWindowButton.MiniaturizeButton);
        HideButton(panel, NSWindowButton.ZoomButton);
        panel.WillClose += OnWillClose;

        NSTextField instructions = NSTextField.CreateWrappingLabel(PairingText.Instructions(ComputerName.Get()));
        instructions.Alignment = NSTextAlignment.Center;
        instructions.PreferredMaxLayoutWidth = (nfloat)(ContentWidth - 48);

        // QR codes must be dark on light whatever the appearance, so the box is white in dark mode too.
        _qrBox = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _qrBox.Layer!.BackgroundColor = new CGColor(1, 1, 1, 1);
        _qrBox.Layer.CornerRadius = 10;
        _qrBox.AccessibilityElement = true;
        _qrBox.AccessibilityRole = NSAccessibilityRoles.ImageRole;
        _qrView = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _qrView.Layer!.MagnificationFilter = CALayer.FilterNearest;
        _qrView.Layer.MinificationFilter = CALayer.FilterNearest;
        _qrView.Layer.ContentsGravity = CALayer.GravityResize;
        _qrView.AccessibilityElement = false;
        _qrBox.AddSubview(_qrView);
        _symbol = new NSImageView
        {
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(96, NSFontWeight.Regular),
        };
        _symbol.AccessibilityElement = false;
        _qrBox.AddSubview(_symbol);
        _qrBoxSize = _qrBox.WidthAnchor.ConstraintEqualTo((nfloat)(QrPoints + (2 * QrPadding)));
        _qrViewSize = _qrView.WidthAnchor.ConstraintEqualTo((nfloat)QrPoints);
        NSLayoutConstraint.ActivateConstraints(
        [
            _qrBoxSize,
            _qrBox.HeightAnchor.ConstraintEqualTo(_qrBox.WidthAnchor),
            _qrViewSize,
            _qrView.HeightAnchor.ConstraintEqualTo(_qrView.WidthAnchor),
            _qrView.CenterXAnchor.ConstraintEqualTo(_qrBox.CenterXAnchor),
            _qrView.CenterYAnchor.ConstraintEqualTo(_qrBox.CenterYAnchor),
            _symbol.CenterXAnchor.ConstraintEqualTo(_qrBox.CenterXAnchor),
            _symbol.CenterYAnchor.ConstraintEqualTo(_qrBox.CenterYAnchor),
        ]);

        _pin = NSTextField.CreateLabel(string.Empty);
        _pin.Font = OrSystem(NSFont.MonospacedSystemFont(40, NSFontWeight.Semibold));
        _pin.Alignment = NSTextAlignment.Center;
        _pin.Selectable = true;

        _manual = Caption("Manual connection (host:port)");
        _endpoints = NSTextField.CreateLabel(string.Empty);
        _endpoints.Font = OrSystem(NSFont.MonospacedDigitSystemFontOfSize(13, NSFontWeight.Regular));
        _endpoints.Alignment = NSTextAlignment.Center;
        _endpoints.Selectable = true;
        _endpoints.AccessibilityLabel = "Manual connection address";

        _countdown = Caption(string.Empty);
        _countdown.Font = OrSystem(NSFont.MonospacedDigitSystemFontOfSize(12, NSFontWeight.Regular));

        _status = NSTextField.CreateWrappingLabel(string.Empty);
        _status.Font = OrSystem(NSFont.SystemFontOfSize(13, NSFontWeight.Medium));
        _status.Alignment = NSTextAlignment.Center;
        _status.PreferredMaxLayoutWidth = (nfloat)(ContentWidth - 48);

        var stack = NSStackView.FromViews([instructions, _qrBox, _pin, _manual, _endpoints, _countdown, _status]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.Spacing = 10;
        stack.EdgeInsets = new NSEdgeInsets(20, 24, 20, 24);
        stack.SetCustomSpacing(16, _qrBox);
        stack.SetCustomSpacing(16, _pin);
        stack.SetCustomSpacing(2, _manual);
        stack.WidthAnchor.ConstraintEqualTo((nfloat)ContentWidth).Active = true;
        panel.ContentView = stack;
        return panel;
    }

    private static NSTextField Caption(string text)
    {
        NSTextField label = NSTextField.CreateLabel(text);
        label.Font = OrSystem(NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize));
        label.TextColor = NSColor.SecondaryLabel;
        label.Alignment = NSTextAlignment.Center;
        return label;
    }

    // The font factories are nullable in the bindings; they only return null for nonsense sizes.
    private static NSFont OrSystem(NSFont? font) => font ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!;

    private static void HideButton(NSWindow window, NSWindowButton kind)
    {
        if (window.StandardWindowButton(kind) is { } button)
        {
            button.Hidden = true;
        }
    }

    private static void EnsureMainThread()
    {
        if (!NSThread.IsMain)
        {
            throw new InvalidOperationException("PairingWindowController must be used on the main thread.");
        }
    }
}
