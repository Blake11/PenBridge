using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CoreFoundation;
using Foundation;
using ObjCRuntime;

namespace PenBridge.Mac.Services;

/// <summary>The user-visible computer name (System Settings ▸ General ▸ Sharing), e.g. "Paul's MacBook Pro".</summary>
public static partial class ComputerName
{
    private const string SystemConfiguration = "/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary><c>SCDynamicStoreCopyComputerName</c>, falling back to the host name.</summary>
    public static string Get()
    {
        nint handle = SCDynamicStoreCopyComputerName(0, 0);
        string? name = handle == 0 ? null : CFString.FromHandle(new NativeHandle(handle), releaseHandle: true);
        return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name;
    }

    [LibraryImport(SystemConfiguration)]
    private static partial nint SCDynamicStoreCopyComputerName(nint store, nint nameEncoding);

    [LibraryImport(SystemConfiguration)]
    internal static partial nint SCDynamicStoreKeyCreateComputerName(nint allocator);

    [LibraryImport(SystemConfiguration)]
    internal static unsafe partial nint SCDynamicStoreCreate(nint allocator, nint name, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callout, StoreContext* context);

    [LibraryImport(SystemConfiguration)]
    internal static partial byte SCDynamicStoreSetNotificationKeys(nint store, nint keys, nint patterns);

    [LibraryImport(SystemConfiguration)]
    internal static partial byte SCDynamicStoreSetDispatchQueue(nint store, nint queue);

    [LibraryImport(CoreFoundationLib)]
    internal static partial void CFRelease(nint handle);

    /// <summary><c>SCDynamicStoreContext</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StoreContext
    {
        public nint Version;
        public nint Info;
        public nint Retain;
        public nint Release;
        public nint CopyDescription;
    }
}

/// <summary>
/// Raises <see cref="Changed"/> on the main thread when the computer name changes: an <c>SCDynamicStore</c>
/// notification on the computer-name key, delivered on the main dispatch queue (no polling).
/// </summary>
public sealed unsafe class ComputerNameWatcher : IDisposable
{
    private GCHandle _self;
    private nint _store;

    public ComputerNameWatcher()
    {
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        var context = new ComputerName.StoreContext { Info = GCHandle.ToIntPtr(_self) };
        using var storeName = new NSString(OsLogLoggerProvider.Subsystem);
        _store = ComputerName.SCDynamicStoreCreate(0, storeName.Handle, &OnStoreChanged, &context);
        if (_store == 0)
        {
            return;
        }

        string key = CFString.FromHandle(new NativeHandle(ComputerName.SCDynamicStoreKeyCreateComputerName(0)), releaseHandle: true) ?? string.Empty;
        using NSArray keys = NSArray.FromStrings(key);
        if (ComputerName.SCDynamicStoreSetNotificationKeys(_store, keys.Handle, 0) == 0
            || ComputerName.SCDynamicStoreSetDispatchQueue(_store, DispatchQueue.MainQueue.Handle) == 0)
        {
            ComputerName.CFRelease(_store);
            _store = 0;
        }
    }

    /// <summary>Raised on the main thread after the computer name changed (read it with <see cref="ComputerName.Get"/>).</summary>
    public event EventHandler? Changed;

    /// <summary>False when the dynamic store couldn't be set up (then the name is only re-read on wake/network change).</summary>
    public bool IsWatching => _store != 0;

    public void Dispose()
    {
        if (_store != 0)
        {
            ComputerName.SCDynamicStoreSetDispatchQueue(_store, 0);
            ComputerName.CFRelease(_store);
            _store = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStoreChanged(nint store, nint changedKeys, nint info)
    {
        try
        {
            if (GCHandle.FromIntPtr(info).Target is ComputerNameWatcher self && self._store == store)
            {
                self.Changed?.Invoke(self, EventArgs.Empty);
            }
        }
#pragma warning disable CA1031 // An exception must not unwind into native code.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
