using Android.Content;
using Android.Net.Nsd;
using Android.Net.Wifi;
using Android.OS;
using Android.Util;
using Java.Net;
using Java.Util.Concurrent;
using PenBridge.Core;
using PenBridge.Core.Net;

namespace PenBridge.Android.Services;

/// <summary>
/// Browses <c>_penbridge._tcp</c> with <see cref="NsdManager"/> and resolves each instance's address and TXT record
/// into a <see cref="DiscoveredServer"/>. A <see cref="WifiManager.MulticastLock"/> is held only while browsing.
/// </summary>
/// <remarks>
/// <para><b>Threading:</b> main thread only. <see cref="Start"/>/<see cref="Stop"/> must be called on it, and
/// <see cref="Changed"/> is raised on it. NSD callbacks arrive on NsdManager's internal thread (legacy listeners) or
/// on the main executor (API 34+ service-info callbacks); both are funnelled onto the main looper, and results of a
/// stopped browse are dropped.</para>
/// <para><b>Resolution:</b> on API 34+ each found instance gets a <c>RegisterServiceInfoCallback</c>, which keeps the
/// address/TXT current until the instance is lost. On API 29–33 instances are resolved with the legacy
/// <c>ResolveService</c>, one at a time (older platforms fail concurrent resolves with <c>AlreadyActive</c>); a service
/// that is found again is re-resolved.</para>
/// <para>Addresses: IPv4 first, then a global IPv6 address; link-local IPv6 is skipped (it needs a scope id).
/// The ClientHost stops browsing in the background (<c>OnStop</c>). The emulator never sees mDNS (NAT).</para>
/// </remarks>
public sealed class MacDiscovery : IDisposable
{
    private const string Tag = "PenBridge";
    private static readonly TimeSpan ResolveRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly NsdManager _nsd;
    private readonly WifiManager.MulticastLock? _multicastLock;
    private readonly IExecutor _mainExecutor;
    private readonly Handler _main;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<NsdServiceInfo> _resolveQueue = new();
    private IReadOnlyList<DiscoveredServer> _servers = [];
    private DiscoveryListener? _listener;
    private int _generation;
    private bool _resolving;
    private bool _disposed;

    public MacDiscovery(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context app = context.ApplicationContext ?? context;
        _nsd = (NsdManager?)app.GetSystemService(Context.NsdService) ?? throw new InvalidOperationException("NSD unavailable.");
        var wifi = (WifiManager?)app.GetSystemService(Context.WifiService);
        _multicastLock = wifi?.CreateMulticastLock("penbridge.discovery");
        _multicastLock?.SetReferenceCounted(false);
        _mainExecutor = app.MainExecutor ?? throw new InvalidOperationException("Main executor unavailable.");
        _main = new Handler(Looper.MainLooper!);
    }

    /// <summary>Raised on the main thread when <see cref="Servers"/>, <see cref="IsBrowsing"/> or <see cref="LastFailure"/> change.</summary>
    public event EventHandler? Changed;

    /// <summary>Resolved servers (snapshot, sorted by name). Includes incompatible versions; check <c>IsCompatible</c>.</summary>
    public IReadOnlyList<DiscoveredServer> Servers => _servers;

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/> while NSD accepted the browse.</summary>
    public bool IsBrowsing { get; private set; }

    /// <summary>Why the last browse failed to start, or null.</summary>
    public NsdFailure? LastFailure { get; private set; }

    /// <summary>Starts browsing (idempotent). Main thread.</summary>
    public void Start()
    {
        EnsureMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener is not null)
        {
            return;
        }

        int generation = ++_generation;
        LastFailure = null;
        _multicastLock?.Acquire();
        var listener = new DiscoveryListener(this, generation);
        _listener = listener;
        try
        {
            _nsd.DiscoverServices(ProtocolInfo.ServiceType, NsdProtocol.DnsSd, listener);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            _listener = null;
            _multicastLock?.Release();
            LastFailure = NsdFailure.InternalError;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stops browsing, releases the multicast lock and clears the list (idempotent). Main thread.</summary>
    public void Stop()
    {
        EnsureMainThread();
        if (_listener is not { } listener)
        {
            return;
        }

        _listener = null;
        _generation++;
        try
        {
            _nsd.StopServiceDiscovery(listener);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // The browse never started (start failed asynchronously); nothing to stop.
        }

        foreach (Entry entry in _entries.Values)
        {
            UnregisterCallback(entry);
        }

        _entries.Clear();
        _resolveQueue.Clear();
        _resolving = false;
        if (_multicastLock is { IsHeld: true })
        {
            _multicastLock.Release();
        }

        IsBrowsing = false;
        PublishServers();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _main.Dispose();
    }

    private static void EnsureMainThread()
    {
        if (Looper.MainLooper is not { IsCurrentThread: true })
        {
            throw new InvalidOperationException("MacDiscovery must be used on the main thread.");
        }
    }

    private bool IsCurrent(int generation) => generation == _generation && _listener is not null;

    // ---- Browse callbacks (marshalled to the main thread) ----

    private void OnDiscoveryStarted(int generation)
    {
        if (IsCurrent(generation))
        {
            IsBrowsing = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnStartFailed(int generation, NsdFailure failure)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        Log.Warn(Tag, "NSD browse failed to start: " + failure);
        _listener = null;
        _generation++;
        if (_multicastLock is { IsHeld: true })
        {
            _multicastLock.Release();
        }

        IsBrowsing = false;
        LastFailure = failure;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnServiceFound(int generation, NsdServiceInfo info)
    {
        if (!IsCurrent(generation) || info.ServiceName is not { Length: > 0 } name)
        {
            return;
        }

        if (!_entries.TryGetValue(name, out Entry? entry))
        {
            entry = new Entry();
            _entries[name] = entry;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            if (entry.Callback is not null)
            {
                return; // already tracked; the callback reports updates
            }

            var callback = new ServiceInfoCallback(this, generation, name);
            try
            {
                _nsd.RegisterServiceInfoCallback(info, _mainExecutor, callback);
                entry.Callback = callback;
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                Log.Warn(Tag, "NSD service-info callback rejected");
            }
        }
        else
        {
            _resolveQueue.Enqueue(info);
            ResolveNext();
        }
    }

    private void OnServiceLost(int generation, string? name)
    {
        if (!IsCurrent(generation) || name is null || !_entries.Remove(name, out Entry? entry))
        {
            return;
        }

        UnregisterCallback(entry);
        if (entry.Server is not null)
        {
            PublishServers();
        }
    }

    // ---- Resolution ----

    private void OnResolved(int generation, string name, NsdServiceInfo info)
    {
        if (!IsCurrent(generation) || !_entries.TryGetValue(name, out Entry? entry))
        {
            return;
        }

        string? host = PickHost(info);
        var txt = new List<KeyValuePair<string, byte[]?>>();
        if (info.Attributes is { } attributes)
        {
            foreach (KeyValuePair<string, byte[]> attribute in attributes)
            {
                txt.Add(new(attribute.Key, attribute.Value));
            }
        }

        DiscoveredServer.TryCreate(name, host, info.Port, txt, out DiscoveredServer? server);
        if (server is null && entry.Server is null)
        {
            return;
        }

        entry.Server = server;
        PublishServers();
    }

    private void OnInfoCallbackLost(int generation, string name)
    {
        if (IsCurrent(generation) && _entries.TryGetValue(name, out Entry? entry) && entry.Server is not null)
        {
            entry.Server = null;
            PublishServers();
        }
    }

    private void ResolveNext()
    {
        if (_resolving || _listener is null || !_resolveQueue.TryDequeue(out NsdServiceInfo? info))
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            return; // API 34+ never queues resolves
        }

        _resolving = true;
        try
        {
            _nsd.ResolveService(info, new ResolveListener(this, _generation, info));
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            _resolving = false;
            ResolveNext();
        }
    }

    private void OnResolveFinished(int generation, NsdServiceInfo original, NsdServiceInfo? resolved, NsdFailure failure)
    {
        if (generation != _generation)
        {
            return;
        }

        string name = original.ServiceName ?? string.Empty;

        _resolving = false;
        if (resolved is not null)
        {
            OnResolved(generation, name, resolved);
        }
        else if (failure == NsdFailure.AlreadyActive && _entries.ContainsKey(name))
        {
            // Older platforms run one resolve at a time per client; retry shortly.
            _resolveQueue.Enqueue(original);
            _resolving = true;
            _main.PostDelayed(
                () =>
                {
                    if (generation == _generation)
                    {
                        _resolving = false;
                        ResolveNext();
                    }
                },
                (long)ResolveRetryDelay.TotalMilliseconds);
            return;
        }

        ResolveNext();
    }

    private void UnregisterCallback(Entry entry)
    {
        if (entry.Callback is { } callback && OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            entry.Callback = null;
            try
            {
                _nsd.UnregisterServiceInfoCallback(callback);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                // Already unregistered by the platform (registration failed or service lost).
            }
        }
    }

    private static string? PickHost(NsdServiceInfo info)
    {
        IList<InetAddress>? addresses = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            addresses = info.HostAddresses;
        }
        else if (info.Host is { } single)
        {
            addresses = [single];
        }

        if (addresses is null)
        {
            return null;
        }

        string? global6 = null;
        foreach (InetAddress address in addresses)
        {
            if (address is Inet4Address v4)
            {
                return v4.HostAddress;
            }

            if (global6 is null && address is Inet6Address v6 && !v6.IsLinkLocalAddress && !v6.IsLoopbackAddress)
            {
                // Strip any "%scope" suffix; a global address doesn't need one.
                string text = v6.HostAddress ?? string.Empty;
                int percent = text.IndexOf('%', StringComparison.Ordinal);
                global6 = percent >= 0 ? text[..percent] : text;
            }
        }

        return string.IsNullOrEmpty(global6) ? null : global6;
    }

    private void PublishServers()
    {
        var list = new List<DiscoveredServer>(_entries.Count);
        foreach (Entry entry in _entries.Values)
        {
            if (entry.Server is not null)
            {
                list.Add(entry.Server);
            }
        }

        list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _servers = list;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Post(Action action) => _main.Post(action);

    private sealed class Entry
    {
        public ServiceInfoCallback? Callback { get; set; }

        public DiscoveredServer? Server { get; set; }
    }

    /// <summary>Legacy browse listener; its callbacks run on NsdManager's thread and are posted to the main looper.</summary>
    private sealed class DiscoveryListener(MacDiscovery owner, int generation) : Java.Lang.Object, NsdManager.IDiscoveryListener
    {
        public void OnDiscoveryStarted(string? serviceType) => owner.Post(() => owner.OnDiscoveryStarted(generation));

        public void OnDiscoveryStopped(string? serviceType)
        {
        }

        public void OnServiceFound(NsdServiceInfo? serviceInfo)
        {
            if (serviceInfo is not null)
            {
                owner.Post(() => owner.OnServiceFound(generation, serviceInfo));
            }
        }

        public void OnServiceLost(NsdServiceInfo? serviceInfo)
        {
            string? name = serviceInfo?.ServiceName;
            owner.Post(() => owner.OnServiceLost(generation, name));
        }

        public void OnStartDiscoveryFailed(string? serviceType, NsdFailure errorCode) =>
            owner.Post(() => owner.OnStartFailed(generation, errorCode));

        public void OnStopDiscoveryFailed(string? serviceType, NsdFailure errorCode)
        {
        }
    }

    /// <summary>API 29–33 one-shot resolve (one listener per request); callbacks are posted to the main looper.</summary>
    private sealed class ResolveListener(MacDiscovery owner, int generation, NsdServiceInfo original) : Java.Lang.Object, NsdManager.IResolveListener
    {
        public void OnServiceResolved(NsdServiceInfo? serviceInfo) =>
            owner.Post(() => owner.OnResolveFinished(generation, original, serviceInfo, NsdFailure.InternalError));

        public void OnResolveFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode) =>
            owner.Post(() => owner.OnResolveFinished(generation, original, null, errorCode));
    }

    /// <summary>API 34+ continuous resolution; registered with the main executor, so callbacks are already on the main thread.</summary>
    private sealed class ServiceInfoCallback(MacDiscovery owner, int generation, string name) : Java.Lang.Object, NsdManager.IServiceInfoCallback
    {
        public void OnServiceInfoCallbackRegistrationFailed(NsdFailure errorCode) =>
            Log.Warn(Tag, "NSD service-info registration failed: " + errorCode);

        public void OnServiceInfoCallbackUnregistered()
        {
        }

        public void OnServiceLost() => owner.OnInfoCallbackLost(generation, name);

        public void OnServiceUpdated(NsdServiceInfo serviceInfo) => owner.OnResolved(generation, name, serviceInfo);
    }
}
