using AppKit;
using Foundation;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;
using PenBridge.Mac.Input;

namespace PenBridge.Mac.Services;

/// <summary>
/// Applies the Mac settings (ADR-0030–0033) and keeps the phone in sync: <see cref="SettingsStore"/> and the
/// displays → the server's input settings and mapping target, the settings snapshot and the display list; and the
/// phone's SettingsUpdate → validated (Core's <see cref="MacSettingValues.ApplyUpdate"/>) → the store.
/// </summary>
/// <remarks>
/// <para><b>Main thread only.</b> Every source is main-thread: store setters (menu, Settings window), the resolver's
/// reconfiguration events, and <see cref="ServerHost.SettingsUpdateRequested"/> (marshalled by the host). Nothing
/// polls; idle costs nothing.</para>
/// <para><b>One truth.</b> The values applied and sent are always <see cref="SettingsStore.ToEntries"/> passed through
/// <see cref="MacSettingValues.ApplyUpdate"/> with the display list the phone gets, so the server, the snapshot and the
/// window agree: a stored TargetDisplay of a display that isn't connected reads as main (the store keeps the UUID
/// for when it returns), and Region is the region of the display the target resolves to.</para>
/// <para><b>Coalescing.</b> Store and display changes queue one apply on the main run loop, so the several writes of
/// one phone update or a reconfiguration become one snapshot; a phone update is answered at once (the snapshot is
/// the acknowledgement, even when nothing changed). Core coalesces the pushes too (latest wins).</para>
/// <para>The target is applied only when it changed, so an unrelated setting never touches the mapper; no display at
/// all stops input at the injector instead (<see cref="CGEventTabletInjector.IsTargetAvailable"/>).</para>
/// </remarks>
public sealed partial class SettingsSync : IDisposable
{
    private readonly SettingsStore _store;
    private readonly DisplayTargetResolver _resolver;
    private readonly ServerHost _host;
    private readonly CGEventTabletInjector _injector;
    private readonly ILogger _log;
    private DisplayListEntry[] _displayList = [];
    private MappingTarget? _appliedTarget;
    private InputSettings? _appliedInput;
    private IReadOnlyList<SettingEntry>? _sentSnapshot;
    private bool _applyQueued;
    private bool _disposed;

    /// <summary>Wires everything and applies the stored settings at once (before the server starts).</summary>
    public SettingsSync(SettingsStore store, DisplayTargetResolver resolver, ServerHost host, CGEventTabletInjector injector, ILogger<SettingsSync> log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(log);
        RequireMainThread();
        _store = store;
        _resolver = resolver;
        _host = host;
        _injector = injector;
        _log = log;

        _store.Changed += OnStoreChanged;
        _resolver.TargetChanged += OnTargetChanged;
        _resolver.DisplaysChanged += OnDisplaysChanged;
        _host.SettingsUpdateRequested += OnSettingsUpdateRequested;

        _resolver.SetTarget(_store.TargetDisplay);
        PublishDisplays();
        Apply(forceSnapshot: true);
    }

    /// <summary>The display list the phone gets (and that validates its TargetDisplay).</summary>
    public IReadOnlyList<DisplayListEntry> DisplayList => _displayList;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnStoreChanged;
        _resolver.TargetChanged -= OnTargetChanged;
        _resolver.DisplaysChanged -= OnDisplaysChanged;
        _host.SettingsUpdateRequested -= OnSettingsUpdateRequested;
    }

    private void OnStoreChanged(object? sender, SettingChangedEventArgs e)
    {
        if (!SettingsStore.IsSyncedKey(e.Key))
        {
            return; // General settings (pause, takeover, run at startup) aren't in the key table
        }

        if (e.Key == SettingsStore.Keys.TargetDisplay)
        {
            // At once, so the menu and window see the resolved target in this same turn.
            _resolver.SetTarget(_store.TargetDisplay);
        }

        QueueApply();
    }

    private void OnTargetChanged(object? sender, EventArgs e) => QueueApply();

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        PublishDisplays();
        QueueApply(); // the TargetDisplay's validity, the region's display and the all-displays target may change
    }

    private void OnSettingsUpdateRequested(object? sender, SettingsUpdateRequestedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        MacSettingValues values = CurrentValues();
        IReadOnlyList<SettingEntry> changed = values.ApplyUpdate(e.Entries, _displayList);
        Write(e.Entries, changed, values);
        Log.UpdateFromPhone(_log, e.Entries.Count, changed.Count);
        Apply(forceSnapshot: true);
    }

    /// <summary>Stores the accepted values of a phone update; the target first, so a Region in it is the new target's.</summary>
    private void Write(IReadOnlyList<SettingEntry> requested, IReadOnlyList<SettingEntry> changed, MacSettingValues values)
    {
        // TargetDisplay and Region are written when the update carried an accepted one, not by "changed": the
        // values read a stored but disconnected display as main, and a Region equal to the old target's would
        // otherwise not reach the new target.
        SettingEntry? target = LastOf(requested, MacSettingKey.TargetDisplay, SettingType.String);
        if (target is { } t && string.Equals(values.TargetDisplay, t.StringValue, StringComparison.Ordinal))
        {
            _store.TargetDisplay = values.TargetDisplay; // also re-targets the resolver (OnStoreChanged)
        }

        // The pair was validated together: write the end that widens the range first so the store's clamp never bites.
        bool fullFirst = values.FullPressureAt > _store.FullPressureAt;
        foreach (SettingEntry entry in changed)
        {
            if (fullFirst && entry.Key == (ushort)MacSettingKey.FullPressureAt)
            {
                _ = _store.TryWrite(entry);
            }
        }

        foreach (SettingEntry entry in changed)
        {
            if (!(fullFirst && entry.Key == (ushort)MacSettingKey.FullPressureAt))
            {
                _ = _store.TryWrite(entry); // false for TargetDisplay and Region, handled here
            }
        }

        SettingEntry? region = LastOf(requested, MacSettingKey.Region, SettingType.Rect);
        string? regionDisplay = RegionDisplayUuid();
        if (region is { } r && regionDisplay is not null
            && MacSettings.TryValidate(r, values, _displayList, out SettingEntry normalized)
            && normalized.RectValue == values.Region)
        {
            _store.SetRegion(regionDisplay, values.Region);
        }
    }

    private static SettingEntry? LastOf(IReadOnlyList<SettingEntry> entries, MacSettingKey key, SettingType type)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Key == (ushort)key && entries[i].Type == type)
            {
                return entries[i];
            }
        }

        return null;
    }

    private void QueueApply()
    {
        if (_applyQueued || _disposed)
        {
            return;
        }

        _applyQueued = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(RunQueuedApply);
    }

    // A main-run-loop block: an apply that ran meanwhile (a phone reply) already covered it, and nothing may throw
    // into the run loop.
    private void RunQueuedApply()
    {
        if (!_applyQueued)
        {
            return;
        }

        try
        {
            Apply(forceSnapshot: false);
        }
#pragma warning disable CA1031 // A run-loop block must not throw; logged instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.ApplyFailed(_log, ex);
        }
    }

    /// <summary>Applies the current values to the server and sends the snapshot if it differs from the last one (or when forced).</summary>
    private void Apply(bool forceSnapshot)
    {
        _applyQueued = false;
        if (_disposed)
        {
            return;
        }

        MacSettingValues values = CurrentValues();
        ApplyTarget(values);

        InputSettings input = values.ToInputSettings();
        if (input != _appliedInput)
        {
            _host.SetInputSettings(input);
            _appliedInput = input;
            Log.InputSettingsApplied(_log, input.StylusMode.ToString(), input.SideButton.ToString(), input.SmartZoom.ToString());
        }

        IReadOnlyList<SettingEntry> snapshot = values.ToEntries();
        if (forceSnapshot || _sentSnapshot is null || !SameEntries(snapshot, _sentSnapshot))
        {
            _host.PublishSettings(snapshot);
            _sentSnapshot = snapshot;
        }
    }

    private void ApplyTarget(MacSettingValues values)
    {
        if (!_resolver.HasDisplay)
        {
            _injector.IsTargetAvailable = false;
            return;
        }

        MappingTarget target = values.TargetDisplay == MacSettings.AllDisplays
            ? AllDisplaysTarget()
            : MappingTarget.ForDisplay(_resolver.Target.Bounds, values.Region);
        if (_appliedTarget is null || !SameTarget(_appliedTarget, target))
        {
            _host.SetTarget(target);
            _appliedTarget = target;
        }

        _injector.IsTargetAvailable = true;
    }

    private MappingTarget AllDisplaysTarget()
    {
        IReadOnlyList<DisplayInfo> displays = _resolver.Displays;
        int count = Math.Min(displays.Count, MappingTarget.MaxDisplays);
        Span<DisplayRect> rects = count <= 16 ? stackalloc DisplayRect[count] : new DisplayRect[count];
        for (int i = 0; i < count; i++)
        {
            rects[i] = displays[i].Bounds;
        }

        return MappingTarget.ForAllDisplays(rects);
    }

    /// <summary>The store's values, validated against the display list (what the server applies and the phone sees).</summary>
    private MacSettingValues CurrentValues()
    {
        var values = new MacSettingValues();
        _ = values.ApplyUpdate(_store.ToEntries(RegionDisplayUuid()), _displayList);
        return values;
    }

    /// <summary>The display a Region belongs to: the resolved target (main for "" or a missing display), none for All displays.</summary>
    private string? RegionDisplayUuid() =>
        _resolver.IsAllDisplays || !_resolver.HasDisplay ? null : _resolver.Target.Uuid;

    private void PublishDisplays()
    {
        IReadOnlyList<DisplayInfo> displays = _resolver.Displays;
        var list = new List<DisplayListEntry>(Math.Min(displays.Count, SettingsWire.MaxDisplays));
        foreach (DisplayInfo d in displays)
        {
            if (list.Count == SettingsWire.MaxDisplays)
            {
                break;
            }

            float width = (float)d.Bounds.Width;
            float height = (float)d.Bounds.Height;
            if (d.Uuid.Length == 0 || d.Uuid == MacSettings.AllDisplays || !float.IsFinite(width) || !(width > 0) || !float.IsFinite(height) || !(height > 0)
                || WireString.Truncate(d.Uuid) != d.Uuid)
            {
                continue;
            }

            list.Add(new DisplayListEntry(d.Uuid, WireString.Truncate(d.Name), d.IsMain, width, height));
        }

        _displayList = [.. list];
        _host.PublishDisplays(_displayList);
    }

    private static bool SameTarget(MappingTarget a, MappingTarget b) =>
        a.IsAllDisplays == b.IsAllDisplays && a.Region == b.Region && a.Bounds == b.Bounds && a.Displays.SequenceEqual(b.Displays);

    private static bool SameEntries(IReadOnlyList<SettingEntry> a, IReadOnlyList<SettingEntry> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireMainThread()
    {
        if (!NSThread.IsMain)
        {
            throw new InvalidOperationException("SettingsSync must be used on the main thread.");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Information, "Settings update from the phone: {Count} entries, {Changed} changed")]
        public static partial void UpdateFromPhone(ILogger logger, int count, int changed);

        [LoggerMessage(2, LogLevel.Information, "Input settings applied: stylus {StylusMode}, side button {SideButton}, smart zoom {SmartZoom}")]
        public static partial void InputSettingsApplied(ILogger logger, string stylusMode, string sideButton, string smartZoom);

        [LoggerMessage(3, LogLevel.Error, "Applying the settings failed")]
        public static partial void ApplyFailed(ILogger logger, Exception exception);
    }
}
