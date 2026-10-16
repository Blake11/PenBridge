using AppKit;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PenBridge.Core.Input;
using PenBridge.Core.Settings;
using PenBridge.Mac.Services;

namespace PenBridge.Mac.Input;

/// <summary>One active display.</summary>
/// <param name="Id">The CGDirectDisplayID. Valid only until the next reconfiguration; persist <paramref name="Uuid"/>.</param>
/// <param name="Uuid">Stable display UUID (<c>CGDisplayCreateUUIDFromDisplayID</c>), the key to remember the choice by.</param>
/// <param name="Name">The localized name (NSScreen), e.g. "Built-in Retina Display".</param>
/// <param name="Bounds">Bounds in global display points (top-left origin, y down), as Core's mapper expects.</param>
/// <param name="IsMain">True for the main display (the one with the menu bar).</param>
public sealed record DisplayInfo(uint Id, string Uuid, string Name, DisplayRect Bounds, bool IsMain);

/// <summary>
/// Resolves the display the phone surface maps to (ADR-0005, ADR-0016, ADR-0031): the main display by default, the
/// user's choice remembered by UUID (falling back to main while that display is missing), or all displays. Follows
/// display reconfiguration (connect, disconnect, resolution, arrangement) without polling.
/// </summary>
/// <remarks>
/// <para><b>Main thread only</b> (it reads NSScreen); the constructor and every member throw otherwise. Events are
/// raised on the main thread.</para>
/// <para>Reconfiguration comes from <c>NSApplicationDidChangeScreenParametersNotification</c>, which AppKit posts on
/// the main thread once a CoreGraphics display reconfiguration has completed (the same source as
/// <c>CGDisplayRegisterReconfigurationCallback</c>, already coalesced and with NSScreen updated). It requires a running
/// NSApplication, which the menu-bar app has.</para>
/// <para>Wiring: <see cref="SettingsSync"/> feeds it the stored TargetDisplay and turns <see cref="Target"/> /
/// <see cref="IsAllDisplays"/> / <see cref="Displays"/> into the server's mapping target; the menu and the Settings
/// window list <see cref="Displays"/>.</para>
/// <para>No display (e.g. lid closed, nothing attached): <see cref="HasDisplay"/> is false, input is stopped at the
/// injector instead of mapping to a placeholder, and it is logged once until a display returns.</para>
/// </remarks>
public sealed class DisplayTargetResolver : IDisposable
{
    private static readonly DisplayRect PlaceholderBounds = new(0, 0, 1, 1);

    private readonly ILogger _logger;
    private readonly NSObject _observer;
    private DisplayInfo[] _displays = [];
    private bool _disposed;
    private bool _loggedNoDisplay;
    private bool _resolvedAll;

    /// <param name="target">The stored TargetDisplay: a display UUID, <see cref="MacSettings.AllDisplays"/>, or null / "" for the main display.</param>
    /// <param name="logger">Diagnostics (target changes only).</param>
    public DisplayTargetResolver(string? target = null, ILogger<DisplayTargetResolver>? logger = null)
    {
        RequireMainThread();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        SetPreference(target);
        Target = new DisplayInfo((uint)CGDisplay.MainDisplayID, string.Empty, "Main display", PlaceholderBounds, IsMain: true);
        _observer = NSNotificationCenter.DefaultCenter.AddObserver(
            NSApplication.DidChangeScreenParametersNotification, _ => Refresh());
        Refresh();
    }

    /// <summary>Raised (main thread) when the target display or its bounds changed. Re-read <see cref="Target"/>.</summary>
    public event EventHandler? TargetChanged;

    /// <summary>Raised (main thread) when the set of active displays or their names/bounds changed.</summary>
    public event EventHandler? DisplaysChanged;

    /// <summary>Active displays, main first as CoreGraphics reports them. Snapshot; replaced on change.</summary>
    public IReadOnlyList<DisplayInfo> Displays => _displays;

    /// <summary>
    /// The resolved target display: the chosen one, else main. With <see cref="IsAllDisplays"/> it is the main
    /// display (the menu's check mark and the region don't use it then). While <see cref="HasDisplay"/> is false it is
    /// the last display seen (or a 1×1 placeholder before any); never map input to it then.
    /// </summary>
    public DisplayInfo Target { get; private set; }

    /// <summary>True when at least one active display exists and <see cref="Target"/> is real.</summary>
    public bool HasDisplay { get; private set; }

    /// <summary>The user's choice (persist this), or null for "main display". Kept while that display is missing.</summary>
    public string? PreferredUuid { get; private set; }

    /// <summary>True when <see cref="PreferredUuid"/> is set but that display is not connected, so main is used.</summary>
    public bool IsUsingFallback { get; private set; }

    /// <summary>The target is every display as one desktop (ADR-0031); <see cref="Displays"/> are the target.</summary>
    public bool IsAllDisplays { get; private set; }

    /// <summary>
    /// Chooses the target: a display UUID, <see cref="MacSettings.AllDisplays"/>, or null / "" for the main display,
    /// and resolves it; raises <see cref="TargetChanged"/> if it moved.
    /// </summary>
    public void SetTarget(string? target)
    {
        RequireMainThread();
        SetPreference(target);
        Resolve();
    }

    /// <summary>Re-enumerates the displays and re-resolves the target. Called automatically on reconfiguration.</summary>
    public void Refresh()
    {
        RequireMainThread();
        if (_disposed)
        {
            return;
        }

        DisplayInfo[] next = Enumerate();
        if (!SameDisplays(_displays, next))
        {
            _displays = next;
            InputLog.DisplaysReconfigured(_logger, next.Length);
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        }

        Resolve();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NSNotificationCenter.DefaultCenter.RemoveObserver(_observer);
        _observer.Dispose();
    }

    private void SetPreference(string? target)
    {
        IsAllDisplays = string.Equals(target, MacSettings.AllDisplays, StringComparison.Ordinal);
        PreferredUuid = IsAllDisplays || string.IsNullOrEmpty(target) ? null : target;
    }

    private static void RequireMainThread()
    {
        if (!NSThread.IsMain)
        {
            throw new InvalidOperationException("DisplayTargetResolver must be used on the main thread.");
        }
    }

    private static DisplayInfo[] Enumerate()
    {
        if (NativeMethods.CGGetActiveDisplayList(0, null, out uint count) != 0 || count == 0)
        {
            return [];
        }

        var ids = new uint[count];
        if (NativeMethods.CGGetActiveDisplayList(count, ids, out count) != 0)
        {
            return [];
        }

        var names = new Dictionary<uint, string>();
        using var screenNumberKey = new NSString("NSScreenNumber"); // the CGDirectDisplayID in deviceDescription
        foreach (NSScreen screen in NSScreen.Screens)
        {
            if (screen.DeviceDescription[screenNumberKey] is NSNumber number)
            {
                names[number.UInt32Value] = screen.LocalizedName;
            }
        }

        uint main = (uint)CGDisplay.MainDisplayID;
        var result = new List<DisplayInfo>((int)count);
        for (int i = 0; i < count; i++)
        {
            uint id = ids[i];
            CGRect b = CGDisplay.GetBounds((int)id);
            if (!(b.Width > 0) || !(b.Height > 0))
            {
                continue;
            }

            string name = names.TryGetValue(id, out string? n) && !string.IsNullOrEmpty(n) ? n : $"Display {id}";
            result.Add(new DisplayInfo(id, GetUuid(id), name, new DisplayRect(b.X, b.Y, b.Width, b.Height), id == main));
        }

        return [.. result];
    }

    /// <summary>The display UUID string, or a per-boot id fallback when CoreGraphics has none (should not happen).</summary>
    private static string GetUuid(uint id)
    {
        IntPtr uuid = NativeMethods.CGDisplayCreateUUIDFromDisplayID(id);
        if (uuid == IntPtr.Zero)
        {
            return "display-id-" + id;
        }

        try
        {
            IntPtr text = NativeMethods.CFUUIDCreateString(IntPtr.Zero, uuid);
            return CFString.FromHandle(text, releaseHandle: true) ?? "display-id-" + id;
        }
        finally
        {
            NativeMethods.CFRelease(uuid);
        }
    }

    private static bool SameDisplays(DisplayInfo[] a, DisplayInfo[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private void Resolve()
    {
        DisplayInfo? chosen = null;
        DisplayInfo? main = null;
        foreach (DisplayInfo d in _displays)
        {
            if (PreferredUuid is not null && string.Equals(d.Uuid, PreferredUuid, StringComparison.OrdinalIgnoreCase))
            {
                chosen = d;
            }

            if (d.IsMain)
            {
                main = d;
            }
        }

        IsUsingFallback = PreferredUuid is not null && chosen is null;
        DisplayInfo? next = chosen ?? main ?? (_displays.Length > 0 ? _displays[0] : null);
        if (next is null)
        {
            if (HasDisplay || !_loggedNoDisplay)
            {
                _loggedNoDisplay = true;
                HasDisplay = false;
                InputLog.NoDisplays(_logger);
                TargetChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        _loggedNoDisplay = false;
        if (next == Target && HasDisplay && _resolvedAll == IsAllDisplays)
        {
            return;
        }

        HasDisplay = true;
        _resolvedAll = IsAllDisplays;

        Target = next;
        DisplayRect r = next.Bounds;
        if (IsAllDisplays)
        {
            InputLog.TargetAllDisplays(_logger, _displays.Length);
        }
        else
        {
            InputLog.TargetDisplay(_logger, next.Name, next.Uuid, r.X, r.Y, r.Width, r.Height, IsUsingFallback ? " (fallback: chosen display missing)" : string.Empty);
        }

        TargetChanged?.Invoke(this, EventArgs.Empty);
    }
}
