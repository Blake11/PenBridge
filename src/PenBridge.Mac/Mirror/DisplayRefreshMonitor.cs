using AppKit;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// The refresh rate of each display, for the mirror's source rate (spec "Size": the target display's refresh, All
/// displays the highest among them, a variable-rate display its maximum) and <c>SetMirrorSource(pixelSize, refreshHz)</c>.
/// Follows display changes without polling.
/// </summary>
/// <remarks>
/// <para><b>Main thread only</b> (it reads NSScreen). A refresh-rate change is a display mode change, which ends in
/// <c>NSApplicationDidChangeScreenParametersNotification</c> like any reconfiguration; the rates are re-read then and
/// <see cref="Changed"/> is raised only when one differs.</para>
/// <para>Rate = <c>NSScreen.maximumFramesPerSecond</c> (the maximum for a ProMotion/VRR display, the mode's rate
/// otherwise), else the current CoreGraphics mode's rate, else 60.</para>
/// </remarks>
public sealed class DisplayRefreshMonitor : IDisposable
{
    /// <summary>Used when nothing reports a rate (some virtual displays).</summary>
    public const int DefaultHz = 60;

    private static readonly NSString ScreenNumberKey = new("NSScreenNumber");

    private readonly ILogger _logger;
    private readonly NSObject _observer;
    private Dictionary<uint, int> _rates = [];
    private bool _disposed;

    public DisplayRefreshMonitor(ILogger<DisplayRefreshMonitor>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _rates = Read();
        _observer = NSNotificationCenter.DefaultCenter.AddObserver(NSApplication.DidChangeScreenParametersNotification, _ => Refresh());
    }

    /// <summary>Raised (main thread) when any display's refresh rate changed, or displays came or went.</summary>
    public event EventHandler? Changed;

    /// <summary>The refresh rate of a display in Hz (rounded); <see cref="DefaultHz"/> for an unknown display.</summary>
    public int RefreshHz(uint displayId) => _rates.TryGetValue(displayId, out int hz) ? hz : DefaultHz;

    /// <summary>The source rate of a target: its display's rate, or the highest of all its displays.</summary>
    public int RefreshHz(MirrorTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        int best = 0;
        foreach (MirrorDisplay d in target.Displays)
        {
            best = Math.Max(best, RefreshHz(d.DisplayId));
        }

        return best > 0 ? best : DefaultHz;
    }

    /// <summary>Re-reads the rates (called on every display reconfiguration).</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        Dictionary<uint, int> next = Read();
        if (Same(_rates, next))
        {
            return;
        }

        _rates = next;
        MirrorLog.RefreshChanged(_logger);
        Changed?.Invoke(this, EventArgs.Empty);
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

    private static Dictionary<uint, int> Read()
    {
        var rates = new Dictionary<uint, int>();
        foreach (NSScreen screen in NSScreen.Screens)
        {
            if (screen.DeviceDescription[ScreenNumberKey] is not NSNumber number)
            {
                continue;
            }

            uint id = number.UInt32Value;
            int hz = (int)screen.MaximumFramesPerSecond;
            if (hz <= 0)
            {
                hz = (int)Math.Round(MirrorNative.ModeRefreshRate(id));
            }

            rates[id] = hz > 0 ? hz : DefaultHz;
        }

        return rates;
    }

    private static bool Same(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (KeyValuePair<uint, int> kv in a)
        {
            if (!b.TryGetValue(kv.Key, out int hz) || hz != kv.Value)
            {
                return false;
            }
        }

        return true;
    }
}
