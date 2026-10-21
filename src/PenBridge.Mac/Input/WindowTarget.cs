using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AppKit;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PenBridge.Core.Input;
using PenBridge.Mac.Services;

namespace PenBridge.Mac.Input;

/// <summary>
/// A picked app window as the mapping target (ADR-0036): follows its moves and resizes through Accessibility
/// notifications and exposes its part of the display that holds most of it, normalized to that display, for the
/// mapper and the tint.
/// </summary>
/// <remarks>
/// <para><b>Main thread only.</b> The AX observer's run-loop source is on the main run loop (common modes, so it keeps
/// working while a menu tracks), and the NSWorkspace observers post on the main thread.</para>
/// <para><b>Event-driven.</b> Window moved, resized, minimised, restored and destroyed come from an
/// <c>AXObserver</c> on the window; app hidden, shown and terminated and Space changes from NSWorkspace. All of them
/// exist only while a window is tracked; nothing polls. The window list is read once per Space change.</para>
/// <para><b>Fallbacks.</b> Minimised, hidden or on another Space: <see cref="Region"/> is the whole display. Closed,
/// its app quit or off every display: tracking stops and the target is the display again.</para>
/// <para><b>State.</b> Not persisted. <see cref="SettingsStore.TargetDisplay"/> names the window's display (written
/// here when it changes), so the phone sees a display target. Any other TargetDisplay or Region write (menu, Settings
/// window, phone, region overlay) clears the window target; the stored regions stay as they are.</para>
/// <para><b>Native refs.</b> The window element and the observer are +1 refs released by <see cref="Clear"/> and
/// <see cref="Dispose"/>; the observer's run-loop source is removed from the main run loop first. The callback finds
/// this instance through a weak GCHandle freed in <see cref="Dispose"/>.</para>
/// </remarks>
public sealed unsafe partial class WindowTarget : IDisposable
{
    /// <summary>Longest a call into a busy app may block the main thread.</summary>
    private const float MessagingTimeoutSeconds = 1f;

    /// <summary>How far an AX frame may differ from the window list's bounds and still be the same window.</summary>
    private const double FrameMatchTolerance = 1.5;

    private const string MovedNotification = "AXWindowMoved";
    private const string ResizedNotification = "AXWindowResized";
    private const string MiniaturizedNotification = "AXWindowMiniaturized";
    private const string DeminiaturizedNotification = "AXWindowDeminiaturized";
    private const string DestroyedNotification = "AXUIElementDestroyed";

    private static readonly NSString PositionAttribute = new("AXPosition");
    private static readonly NSString SizeAttribute = new("AXSize");
    private static readonly NSString WindowsAttribute = new("AXWindows");
    private static readonly NSString MinimizedAttribute = new("AXMinimized");
    private static readonly NSString[] PreferredWindowAttributes = [new("AXMainWindow"), new("AXFocusedWindow")];

    /// <summary><c>NSWorkspaceApplicationKey</c>: the NSRunningApplication in an NSWorkspace notification's userInfo.</summary>
    private static readonly NSString ApplicationKey = new("NSWorkspaceApplicationKey");

    private static readonly NSString[] WindowNotifications =
    [
        new(MovedNotification),
        new(ResizedNotification),
        new(MiniaturizedNotification),
        new(DeminiaturizedNotification),
        new(DestroyedNotification),
    ];

    private readonly SettingsStore _store;
    private readonly DisplayTargetResolver _resolver;
    private readonly ILogger _log;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly List<NSObject> _workspaceObservers = [];
    private GCHandle _self;
    private IntPtr _window;
    private IntPtr _observer;
    private IntPtr _source;
    private int _pid;
    private uint _number;
    private DisplayRect _frame;
    private NormalizedRect _region = NormalizedRect.Full;
    private bool _minimized;
    private bool _hidden;
    private bool _offSpace;
    private bool _shown;
    private bool _writingTarget;
    private bool _disposed;

    public WindowTarget(SettingsStore store, DisplayTargetResolver resolver, ILogger<WindowTarget>? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        RequireMainThread();
        _store = store;
        _resolver = resolver;
        _log = (ILogger?)log ?? NullLogger.Instance;
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        _store.Changed += OnStoreChanged;
        _resolver.DisplaysChanged += OnDisplaysChanged;
    }

    /// <summary>
    /// Raised (main thread) when the window target starts or stops, its display changes, or it is shown or hidden
    /// (minimised, app hidden, other Space). Apply at once.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised (main thread) for each move or resize on the same display while shown: only <see cref="Region"/> changed.
    /// Arrives in bursts while the user drags; coalesce what goes to the phone.
    /// </summary>
    public event EventHandler? FrameChanged;

    /// <summary>True while a window is the target.</summary>
    public bool IsActive => _window != IntPtr.Zero;

    /// <summary>The window's app name while <see cref="IsActive"/>.</summary>
    public string? AppName { get; private set; }

    /// <summary>The display holding most of the window while <see cref="IsActive"/>.</summary>
    public DisplayInfo? Display { get; private set; }

    /// <summary>True while the window is visible on the current Space (not minimised or hidden).</summary>
    public bool IsShown => _shown;

    /// <summary>
    /// The window's part of <see cref="Display"/>, normalized to it (top-left origin, y down); the full display while
    /// the window isn't shown or no window is the target.
    /// </summary>
    public NormalizedRect Region => IsActive && _shown ? _region : NormalizedRect.Full;

    /// <summary>
    /// Makes a window from the window list the target: finds its Accessibility element by frame and starts observing
    /// it. Replaces a previous window target. Needs the Accessibility grant.
    /// </summary>
    /// <returns>False if the window can't be followed (no grant, the app exposes no matching AX window, off every display).</returns>
    public bool Track(WindowListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RequireMainThread();
        if (_disposed)
        {
            return false;
        }

        // Observe the new window first: if it can't be followed, the current target stays as it is.
        if (!TryObserve(entry, out Observed next))
        {
            return false;
        }

        DisplayRect frame = ReadFrame(next.Window) ?? entry.Bounds;
        if (!FitsADisplay(frame))
        {
            Release(next);
            return false;
        }

        Stop(StopReason.Replaced, raise: false);
        _window = next.Window;
        _observer = next.Observer;
        _source = next.Source;
        _pid = entry.Pid;
        _number = entry.Number;
        using (NSRunningApplication? app = NSRunningApplication.GetRunningApplication(entry.Pid))
        {
            AppName = string.IsNullOrEmpty(app?.LocalizedName) ? entry.OwnerName : app!.LocalizedName;
            _hidden = app?.Hidden ?? false;
        }

        _frame = frame;
        _minimized = ReadMinimized(_window);
        _offSpace = false;
        ObserveWorkspace();
        _ = Recompute(initial: true); // fits (checked above), so a display is set
        Log.Tracking(_log, AppName ?? string.Empty, Display!.Name, _frame.X, _frame.Y, _frame.Width, _frame.Height);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Stops following the window; the target is its display again. No-op without a window target.</summary>
    public void Clear()
    {
        RequireMainThread();
        Stop(StopReason.Cleared, raise: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop(StopReason.Cleared, raise: false);
        _store.Changed -= OnStoreChanged;
        _resolver.DisplaysChanged -= OnDisplaysChanged;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    // ---------------------------------------------------------------- native observation

    /// <summary>A window element with its observer and run-loop source (on the main run loop); both refs +1.</summary>
    private readonly record struct Observed(IntPtr Window, IntPtr Observer, IntPtr Source);

    private bool TryObserve(WindowListEntry entry, out Observed observed)
    {
        observed = default;
        IntPtr app = NativeMethods.AXUIElementCreateApplication(entry.Pid);
        if (app == IntPtr.Zero)
        {
            return false;
        }

        IntPtr window = IntPtr.Zero;
        try
        {
            _ = NativeMethods.AXUIElementSetMessagingTimeout(app, MessagingTimeoutSeconds);
            window = FindWindow(app, entry.Bounds, out int matches);
            if (window == IntPtr.Zero)
            {
                Log.NoAxWindow(_log, entry.OwnerName);
                return false;
            }

            if (matches > 1)
            {
                Log.AmbiguousWindow(_log, entry.OwnerName, matches);
            }

            _ = NativeMethods.AXUIElementSetMessagingTimeout(window, MessagingTimeoutSeconds);
            int error = NativeMethods.AXObserverCreate(entry.Pid, &OnNotification, out IntPtr observer);
            if (error != 0 || observer == IntPtr.Zero)
            {
                Log.ObserverFailed(_log, entry.OwnerName, error);
                return false;
            }

            IntPtr refcon = GCHandle.ToIntPtr(_self);
            foreach (NSString name in WindowNotifications)
            {
                error = NativeMethods.AXObserverAddNotification(observer, window, name.Handle, refcon);
                if (error != 0)
                {
                    Log.NotificationFailed(_log, name.ToString(), error);
                }
            }

            IntPtr source = NativeMethods.AXObserverGetRunLoopSource(observer);
            if (source != IntPtr.Zero)
            {
                NativeMethods.CFRunLoopAddSource(NativeMethods.CFRunLoopGetMain(), source, CommonModes);
            }

            observed = new Observed(window, observer, source);
            window = IntPtr.Zero; // owned by observed now
            return true;
        }
        finally
        {
            if (window != IntPtr.Zero)
            {
                NativeMethods.CFRelease(window);
            }

            NativeMethods.CFRelease(app);
        }
    }

    // Undoes TryObserve: notifications removed, source off the main run loop, both refs released.
    private static void Release(Observed observed)
    {
        foreach (NSString name in WindowNotifications)
        {
            _ = NativeMethods.AXObserverRemoveNotification(observed.Observer, observed.Window, name.Handle);
        }

        if (observed.Source != IntPtr.Zero)
        {
            NativeMethods.CFRunLoopRemoveSource(NativeMethods.CFRunLoopGetMain(), observed.Source, CommonModes);
        }

        NativeMethods.CFRelease(observed.Observer);
        NativeMethods.CFRelease(observed.Window);
    }

    private bool FitsADisplay(DisplayRect frame)
    {
        IReadOnlyList<DisplayInfo> displays = _resolver.Displays;
        int count = displays.Count;
        Span<DisplayRect> rects = count <= 16 ? stackalloc DisplayRect[count] : new DisplayRect[count];
        for (int i = 0; i < count; i++)
        {
            rects[i] = displays[i].Bounds;
        }

        return WindowTargetGeometry.TryFit(frame, rects, out _, out _);
    }

    private static IntPtr CommonModes => CFRunLoop.ModeCommon.Handle;

    // The app's AX window whose frame matches the window list's bounds (the window list has no AX handle and no
    // titles without Screen Recording). Several windows with that frame: the app's main window, then its focused
    // window, if one of them matches; else the first match, front to back. +1 retained, or zero.
    private static IntPtr FindWindow(IntPtr app, DisplayRect bounds, out int matches)
    {
        matches = 0;
        if (NativeMethods.AXUIElementCopyAttributeValue(app, WindowsAttribute.Handle, out IntPtr windows) != 0 || windows == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            IntPtr first = IntPtr.Zero;
            nint count = NativeMethods.CFArrayGetCount(windows);
            for (nint i = 0; i < count; i++)
            {
                IntPtr candidate = NativeMethods.CFArrayGetValueAtIndex(windows, i);
                if (candidate != IntPtr.Zero && Matches(candidate, bounds))
                {
                    matches++;
                    if (first == IntPtr.Zero)
                    {
                        first = candidate;
                    }
                }
            }

            if (matches > 1)
            {
                foreach (NSString attribute in PreferredWindowAttributes)
                {
                    if (NativeMethods.AXUIElementCopyAttributeValue(app, attribute.Handle, out IntPtr preferred) == 0 && preferred != IntPtr.Zero)
                    {
                        if (Matches(preferred, bounds))
                        {
                            return preferred; // already +1 from the copy
                        }

                        NativeMethods.CFRelease(preferred);
                    }
                }
            }

            return first == IntPtr.Zero ? IntPtr.Zero : NativeMethods.CFRetain(first);
        }
        finally
        {
            NativeMethods.CFRelease(windows);
        }
    }

    private static bool Matches(IntPtr element, DisplayRect bounds) =>
        ReadFrame(element) is { } frame
        && Math.Abs(frame.X - bounds.X) <= FrameMatchTolerance && Math.Abs(frame.Y - bounds.Y) <= FrameMatchTolerance
        && Math.Abs(frame.Width - bounds.Width) <= FrameMatchTolerance && Math.Abs(frame.Height - bounds.Height) <= FrameMatchTolerance;

    /// <summary>The element's AXPosition and AXSize: global points, top-left origin (the window list's convention).</summary>
    private static DisplayRect? ReadFrame(IntPtr element)
    {
        if (NativeMethods.AXUIElementCopyAttributeValue(element, PositionAttribute.Handle, out IntPtr position) != 0 || position == IntPtr.Zero)
        {
            return null;
        }

        byte gotPoint;
        NativePoint point;
        try
        {
            gotPoint = NativeMethods.AXValueGetPoint(position, NativeMethods.AXValueCGPointType, out point);
        }
        finally
        {
            NativeMethods.CFRelease(position);
        }

        if (gotPoint == 0 || NativeMethods.AXUIElementCopyAttributeValue(element, SizeAttribute.Handle, out IntPtr size) != 0 || size == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (NativeMethods.AXValueGetSize(size, NativeMethods.AXValueCGSizeType, out NativeSize s) == 0)
            {
                return null;
            }

            var rect = new DisplayRect(point.X, point.Y, s.Width, s.Height);
            return double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height) ? rect : null;
        }
        finally
        {
            NativeMethods.CFRelease(size);
        }
    }

    private static bool ReadMinimized(IntPtr element)
    {
        if (NativeMethods.AXUIElementCopyAttributeValue(element, MinimizedAttribute.Handle, out IntPtr value) != 0 || value == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NativeMethods.CFGetTypeID(value) == NativeMethods.CFBooleanGetTypeID() && NativeMethods.CFBooleanGetValue(value) != 0;
        }
        finally
        {
            NativeMethods.CFRelease(value);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNotification(IntPtr observer, IntPtr element, IntPtr notification, IntPtr refcon)
    {
        try
        {
            if (refcon != IntPtr.Zero && GCHandle.FromIntPtr(refcon).Target is WindowTarget self
                && observer != IntPtr.Zero && self._observer == observer)
            {
                self.OnAxNotification(CFString.FromHandle(notification));
            }
        }
#pragma warning disable CA1031 // An exception must not unwind into native code.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void OnAxNotification(string? name)
    {
        switch (name)
        {
            case DestroyedNotification:
                // Not inside the observer's own callback: tear it down from the next run-loop turn.
                IntPtr window = _window;
                NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                {
                    if (!_disposed && _window == window && window != IntPtr.Zero)
                    {
                        Stop(StopReason.Closed, raise: true);
                    }
                });
                return;
            case MiniaturizedNotification:
                _minimized = true;
                break;
            case DeminiaturizedNotification:
                // Restoring brings the window onto the current Space.
                _minimized = false;
                _offSpace = false;
                break;
            default:
                if (ReadFrame(_window) is { } frame)
                {
                    _frame = frame;
                }

                break;
        }

        _ = Recompute(initial: false);
    }

    private void ObserveWorkspace()
    {
        NSNotificationCenter center = NSWorkspace.SharedWorkspace.NotificationCenter;
        _workspaceObservers.Add(center.AddObserver(NSWorkspace.DidTerminateApplicationNotification, n =>
        {
            if (IsOurApp(n))
            {
                Stop(StopReason.AppQuit, raise: true);
            }
        }));
        _workspaceObservers.Add(center.AddObserver(NSWorkspace.DidHideApplicationNotification, n => SetHidden(n, true)));
        _workspaceObservers.Add(center.AddObserver(NSWorkspace.DidUnhideApplicationNotification, n => SetHidden(n, false)));
        _workspaceObservers.Add(center.AddObserver(NSWorkspace.ActiveSpaceDidChangeNotification, _ => OnSpaceChanged()));
    }

    private bool IsOurApp(NSNotification n) =>
        IsActive && n.UserInfo?[ApplicationKey] is NSRunningApplication app && app.ProcessIdentifier == _pid;

    private void SetHidden(NSNotification n, bool hidden)
    {
        if (IsOurApp(n) && _hidden != hidden)
        {
            _hidden = hidden;
            if (!hidden && !_minimized)
            {
                // A Space change while hidden couldn't tell where the window is: ask again now it's shown.
                _offSpace = WindowList.IsOnScreen(_number) == false;
            }

            _ = Recompute(initial: false);
        }
    }

    // A minimised or hidden window is off screen on every Space, so the window list can't tell where it is; then
    // assume this Space (deminiaturize and unhide re-check). Otherwise the window list's answer.
    private void OnSpaceChanged()
    {
        if (!IsActive)
        {
            return;
        }

        _offSpace = !_minimized && !_hidden && WindowList.IsOnScreen(_number) == false;
        _ = Recompute(initial: false);
    }

    private void Stop(StopReason reason, bool raise)
    {
        if (_window == IntPtr.Zero)
        {
            return;
        }

        Release(new Observed(_window, _observer, _source));
        _source = IntPtr.Zero; // owned by the observer
        _observer = IntPtr.Zero;
        _window = IntPtr.Zero;

        NSNotificationCenter center = NSWorkspace.SharedWorkspace.NotificationCenter;
        foreach (NSObject token in _workspaceObservers)
        {
            center.RemoveObserver(token);
            token.Dispose();
        }

        _workspaceObservers.Clear();
        Log.Stopped(_log, AppName ?? string.Empty, reason.ToString());
        AppName = null;
        Display = null;
        _region = NormalizedRect.Full;
        _shown = false;
        _pid = 0;
        _number = 0;
        if (raise)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------- geometry and events

    /// <summary>
    /// Re-derives the display, region and shown state from the frame and raises <see cref="Changed"/> or
    /// <see cref="FrameChanged"/> (not when <paramref name="initial"/>). False if the window is on no display and its
    /// last display is gone (tracking stopped unless initial).
    /// </summary>
    private bool Recompute(bool initial)
    {
        if (!IsActive)
        {
            return false;
        }

        IReadOnlyList<DisplayInfo> displays = _resolver.Displays;
        int count = displays.Count;
        Span<DisplayRect> rects = count <= 16 ? stackalloc DisplayRect[count] : new DisplayRect[count];
        for (int i = 0; i < count; i++)
        {
            rects[i] = displays[i].Bounds;
        }

        bool fits = WindowTargetGeometry.TryFit(_frame, rects, out int index, out NormalizedRect region);
        DisplayInfo? display = fits ? displays[index] : SameDisplay(displays, Display);
        if (display is null)
        {
            if (!initial)
            {
                Stop(StopReason.OffDisplays, raise: true);
            }

            return false;
        }

        // Off every display (dragged away) counts as not shown: the pad maps to the last display.
        bool shown = fits && !_minimized && !_hidden && !_offSpace;
        NormalizedRect next = fits ? region : NormalizedRect.Full;
        bool displayChanged = Display != display;
        bool shownChanged = shown != _shown;
        bool regionChanged = next != _region;
        Display = display;
        _region = next;
        _shown = shown;
        if (displayChanged)
        {
            NameTargetDisplay(display);
        }

        if (initial)
        {
            return true;
        }

        if (displayChanged || shownChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else if (regionChanged && shown)
        {
            FrameChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    private static DisplayInfo? SameDisplay(IReadOnlyList<DisplayInfo> displays, DisplayInfo? display)
    {
        if (display is null)
        {
            return null;
        }

        foreach (DisplayInfo d in displays)
        {
            if (string.Equals(d.Uuid, display.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }

        return null;
    }

    // TargetDisplay keeps naming the window's display (ADR-0036): the phone sees it, and it's the fallback.
    private void NameTargetDisplay(DisplayInfo display)
    {
        string stored = _store.TargetDisplay;
        if (string.Equals(stored, display.Uuid, StringComparison.OrdinalIgnoreCase) || (stored.Length == 0 && display.IsMain))
        {
            return;
        }

        _writingTarget = true;
        try
        {
            _store.TargetDisplay = display.Uuid;
        }
        finally
        {
            _writingTarget = false;
        }
    }

    private void OnStoreChanged(object? sender, SettingChangedEventArgs e)
    {
        if (!_writingTarget && IsActive && e.Key is SettingsStore.Keys.TargetDisplay or SettingsStore.Keys.Region)
        {
            Stop(StopReason.DisplayOrRegionPicked, raise: true);
        }
    }

    // A reconfiguration can move the window (macOS re-homes windows of an unplugged display) and changes the bounds.
    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (!IsActive)
        {
            return;
        }

        if (ReadFrame(_window) is { } frame)
        {
            _frame = frame;
        }

        _ = Recompute(initial: false);
    }

    private static void RequireMainThread()
    {
        if (!NSThread.IsMain)
        {
            throw new InvalidOperationException("WindowTarget must be used on the main thread.");
        }
    }

    private enum StopReason
    {
        Cleared,
        Replaced,
        Closed,
        AppQuit,
        OffDisplays,
        DisplayOrRegionPicked,
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Information, "Window target: {App} on {Display} at {X}, {Y} ({Width} × {Height} pt)")]
        public static partial void Tracking(ILogger logger, string app, string display, double x, double y, double width, double height);

        [LoggerMessage(2, LogLevel.Information, "Window target ended ({Reason}): {App}")]
        public static partial void Stopped(ILogger logger, string app, string reason);

        [LoggerMessage(3, LogLevel.Warning, "Window target: no Accessibility window of {App} matches the picked frame")]
        public static partial void NoAxWindow(ILogger logger, string app);

        [LoggerMessage(4, LogLevel.Warning, "Window target: AXObserverCreate for {App} failed ({Error})")]
        public static partial void ObserverFailed(ILogger logger, string app, int error);

        [LoggerMessage(6, LogLevel.Information, "Window target: {Count} windows of {App} have the picked frame; preferring the main or focused one")]
        public static partial void AmbiguousWindow(ILogger logger, string app, int count);

        [LoggerMessage(5, LogLevel.Debug, "Window target: observing {Notification} failed ({Error})")]
        public static partial void NotificationFailed(ILogger logger, string notification, int error);
    }
}
