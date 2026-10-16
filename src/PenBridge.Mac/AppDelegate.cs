using AppKit;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using PenBridge.Mac.Input;
using PenBridge.Mac.Pairing;
using PenBridge.Mac.Services;
using PenBridge.Mac.Settings;
using PenBridge.Mac.Shell;

namespace PenBridge.Mac;

/// <summary>
/// Composes the menu-bar app: settings (store and Settings window), Keychain, os_log, the CGEvent injector and display resolver, the
/// <see cref="ServerHost"/>, the status item and the global pause hotkey. Quits cleanly (Bye to the phone, advert
/// withdrawn, nothing left pressed).
/// </summary>
/// <remarks>
/// Launch never blocks the main thread: everything constructed here is cheap, and <see cref="ServerHost.StartAsync"/>
/// reads the Keychain on a worker thread (the first read by a newly signed bundle can take tens of seconds). The menu
/// shows "Starting…" until it completes.
/// </remarks>
[Register("AppDelegate")]
public sealed partial class AppDelegate : NSApplicationDelegate
{
    /// <summary>Upper bound for a clean shutdown; quit proceeds anyway after it.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>NSUserDefaults flag: the system Accessibility prompt was shown once (first launch without the grant).</summary>
    private const string AccessibilityPromptedKey = "AccessibilityPrompted";

    private OsLogLoggerFactory? _loggers;
    private ILogger? _log;
    private SettingsStore? _settings;
    private CGEventTabletInjector? _injector;
    private DisplayTargetResolver? _resolver;
    private IDisposable? _displayBinding;
    private ServerHost? _host;
    private PairingWindowController? _pairing;
    private GlobalHotkey? _hotkey;
    private SettingsWindowController? _settingsWindow;
    private StatusItemController? _statusItem;
    private bool _terminating;
    private bool _replied;

    public override void DidFinishLaunching(NSNotification notification)
    {
        NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Accessory;

#if DEBUG
        _loggers = new OsLogLoggerFactory(LogLevel.Debug);
#else
        _loggers = new OsLogLoggerFactory(LogLevel.Information);
#endif
        _log = _loggers.CreateLogger<AppDelegate>();
        _settings = new SettingsStore();
        _injector = new CGEventTabletInjector(_loggers.CreateLogger<CGEventTabletInjector>());
        _resolver = new DisplayTargetResolver(_settings.TargetDisplayUuid, _loggers.CreateLogger<DisplayTargetResolver>());
        _host = new ServerHost(_injector, SecretStoreFactory.Create(_loggers), _settings, _loggers);
        _displayBinding = _resolver.Bind(_host, _injector);
        _pairing = new PairingWindowController(_host);

        _hotkey = new GlobalHotkey(_loggers.CreateLogger<GlobalHotkey>());
        _hotkey.Pressed += OnHotkeyPressed;
        _hotkey.Register();

        _settingsWindow = new SettingsWindowController(_settings, _resolver, _host);
        _statusItem = new StatusItemController(_host, _injector, _resolver, _settings, _pairing, _hotkey, _settingsWindow, _loggers.CreateLogger<StatusItemController>());
        Log.Launched(_log, NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString() ?? "?");

        PromptAccessibilityOnce();
        _ = StartServerAsync();
    }

    public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender)
    {
        if (_host is null)
        {
            return NSApplicationTerminateReply.Now;
        }

        if (!_terminating)
        {
            _terminating = true;
            DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, ShutdownTimeout), () =>
            {
                if (!_replied && _log is not null)
                {
                    Log.ShutdownTimedOut(_log, ShutdownTimeout.TotalSeconds);
                }

                Reply(sender);
            });
            _ = ShutdownAsync(sender);
        }

        return NSApplicationTerminateReply.Later;
    }

    // The system prompt appears once, on the first launch without the grant; afterwards "Grant Accessibility…" in the
    // menu opens only the Settings pane, so the two UIs never show together.
    private void PromptAccessibilityOnce()
    {
        NSUserDefaults defaults = NSUserDefaults.StandardUserDefaults;
        if (_injector!.IsAccessibilityTrusted || defaults.BoolForKey(AccessibilityPromptedKey))
        {
            return;
        }

        defaults.SetBool(true, AccessibilityPromptedKey);
        _injector.RequestAccessibility();
    }

    private async Task StartServerAsync()
    {
        _statusItem!.IsStarting = true;
        try
        {
            await _host!.StartAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // ServerHost reports start failures in its state; anything else is logged, never thrown into the run loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.StartFailed(_log!, ex);
        }
        finally
        {
            if (!_terminating)
            {
                _statusItem.IsStarting = false;
            }
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_host is null || _statusItem is null || _terminating)
        {
            return;
        }

        _host.SetPaused(!_host.State.Paused);
        _statusItem.FlashPauseState();
    }

    private async Task ShutdownAsync(NSApplication app)
    {
        try
        {
            if (_hotkey is not null)
            {
                _hotkey.Pressed -= OnHotkeyPressed;
                _hotkey.Dispose();
            }

            _pairing?.Dispose(); // closes the panel and the server's pairing window
            _settingsWindow?.Dispose(); // closes the Settings window and a region overlay

            // Bye(Shutdown) to the phone, advert withdrawn, injector released by the server's session teardown.
            await _host!.DisposeAsync().ConfigureAwait(true);
            _displayBinding?.Dispose();
            _resolver?.Dispose();
            _injector?.Dispose();
            _statusItem?.Dispose();
            Log.ShutDown(_log!);
        }
#pragma warning disable CA1031 // Quitting must always complete.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.ShutdownFailed(_log!, ex);
        }
        finally
        {
            Reply(app);
        }
    }

    private void Reply(NSApplication app)
    {
        if (_replied)
        {
            return;
        }

        _replied = true;
        // The loggers stay alive: a late log line from a timed-out shutdown must not hit a disposed os_log handle.
        app.ReplyToApplicationShouldTerminate(true);
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Information, "PenBridge {Version} launched")]
        public static partial void Launched(ILogger logger, string version);

        [LoggerMessage(2, LogLevel.Error, "Starting the server failed")]
        public static partial void StartFailed(ILogger logger, Exception exception);

        [LoggerMessage(3, LogLevel.Information, "Shut down cleanly")]
        public static partial void ShutDown(ILogger logger);

        [LoggerMessage(4, LogLevel.Error, "Shutdown failed; quitting anyway")]
        public static partial void ShutdownFailed(ILogger logger, Exception exception);

        [LoggerMessage(5, LogLevel.Warning, "Shutdown did not finish within {Seconds} s; quitting anyway")]
        public static partial void ShutdownTimedOut(ILogger logger, double seconds);
    }
}
