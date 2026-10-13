using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace PenBridge.Mac.Shell;

/// <summary>
/// The global pause hotkey ⌃⌥⌘P (ADR-0016) via Carbon <c>RegisterEventHotKey</c>, which needs neither Accessibility
/// nor Input Monitoring: the window server delivers the registered combination to this app only, and the keystroke is
/// consumed (other apps don't see it).
/// </summary>
/// <remarks>
/// <para>Main thread only. Carbon dispatches <c>kEventHotKeyPressed</c> through the application event target on the
/// main run loop (which NSApplication runs), so <see cref="Pressed"/> is raised on the main thread. Event-driven, no
/// polling.</para>
/// <para>NativeAOT/trim safe: blittable <see cref="DllImportAttribute"/> signatures and a static
/// <see cref="UnmanagedCallersOnlyAttribute"/> callback that finds the instance through a <see cref="GCHandle"/> passed
/// as the handler's user data.</para>
/// </remarks>
public sealed unsafe partial class GlobalHotkey : IDisposable
{
    /// <summary>Menu text of the shortcut, for display next to "Pause input".</summary>
    public const string DisplayText = "⌃⌥⌘P";

    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    private const uint KeyCodeP = 0x23; // kVK_ANSI_P (layout position, like every hotkey API)
    private const uint CmdKey = 1 << 8;
    private const uint OptionKey = 1 << 11;
    private const uint ControlKey = 1 << 12;

    private const uint EventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint EventHotKeyPressed = 5; // kEventHotKeyPressed
    private const uint EventParamDirectObject = 0x2D2D2D2D; // '----'
    private const uint TypeEventHotKeyId = 0x686B6964; // 'hkid'
    private const uint Signature = 0x5042686B; // 'PBhk'
    private const uint HotKeyId = 1;

    private const int NoErr = 0;
    private const int EventNotHandledErr = -9874;

    private readonly ILogger _log;
    private GCHandle _self;
    private IntPtr _handlerRef;
    private IntPtr _hotKeyRef;

    public GlobalHotkey(ILogger<GlobalHotkey> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Raised on the main thread when ⌃⌥⌘P is pressed.</summary>
    public event EventHandler? Pressed;

    /// <summary>True when the hotkey is registered (another app may already own the combination).</summary>
    public bool IsRegistered => _hotKeyRef != IntPtr.Zero;

    /// <summary>Installs the handler and registers ⌃⌥⌘P; false (logged) when the combination is taken.</summary>
    public bool Register()
    {
        if (IsRegistered)
        {
            return true;
        }

        IntPtr target = GetApplicationEventTarget();
        if (_handlerRef == IntPtr.Zero)
        {
            _self = GCHandle.Alloc(this);
            EventTypeSpec spec = new(EventClassKeyboard, EventHotKeyPressed);
            IntPtr handlerRef;
            int status = InstallEventHandler(target, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&OnHotKeyEvent, 1, &spec, GCHandle.ToIntPtr(_self), &handlerRef);
            if (status != NoErr)
            {
                _self.Free();
                Log.InstallFailed(_log, status);
                return false;
            }

            _handlerRef = handlerRef;
        }

        IntPtr hotKeyRef;
        int registered = RegisterEventHotKey(KeyCodeP, CmdKey | OptionKey | ControlKey, new EventHotKeyId(Signature, HotKeyId), target, 0, &hotKeyRef);
        if (registered != NoErr)
        {
            Log.RegisterFailed(_log, DisplayText, registered);
            return false;
        }

        _hotKeyRef = hotKeyRef;
        Log.Registered(_log, DisplayText);
        return true;
    }

    public void Dispose()
    {
        if (_hotKeyRef != IntPtr.Zero)
        {
            _ = UnregisterEventHotKey(_hotKeyRef);
            _hotKeyRef = IntPtr.Zero;
        }

        if (_handlerRef != IntPtr.Zero)
        {
            _ = RemoveEventHandler(_handlerRef);
            _handlerRef = IntPtr.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int OnHotKeyEvent(IntPtr callRef, IntPtr eventRef, IntPtr userData)
    {
        try
        {
            EventHotKeyId id;
            int status = GetEventParameter(eventRef, EventParamDirectObject, TypeEventHotKeyId, IntPtr.Zero, (nuint)sizeof(EventHotKeyId), IntPtr.Zero, &id);
            if (status != NoErr || id.Signature != Signature || id.Id != HotKeyId)
            {
                return EventNotHandledErr;
            }

            if (GCHandle.FromIntPtr(userData).Target is GlobalHotkey self)
            {
                self.Pressed?.Invoke(self, EventArgs.Empty);
            }

            return NoErr;
        }
#pragma warning disable CA1031 // An exception must never unwind into Carbon's dispatcher (it would crash the process).
        catch (Exception)
#pragma warning restore CA1031
        {
            return NoErr;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct EventTypeSpec(uint eventClass, uint eventKind)
    {
        public readonly uint EventClass = eventClass;
        public readonly uint EventKind = eventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct EventHotKeyId(uint signature, uint id)
    {
        public readonly uint Signature = signature;
        public readonly uint Id = id;
    }

    [DllImport(Carbon)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(IntPtr target, IntPtr handler, uint numTypes, EventTypeSpec* list, IntPtr userData, IntPtr* handlerRef);

    [DllImport(Carbon)]
    private static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, IntPtr target, uint options, IntPtr* hotKeyRef);

    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(IntPtr eventRef, uint name, uint desiredType, IntPtr actualType, nuint bufferSize, IntPtr actualSize, void* data);

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Information, "Global hotkey {Shortcut} registered")]
        public static partial void Registered(ILogger logger, string shortcut);

        [LoggerMessage(2, LogLevel.Warning, "Global hotkey {Shortcut} could not be registered (OSStatus {Status}); another app may own it")]
        public static partial void RegisterFailed(ILogger logger, string shortcut, int status);

        [LoggerMessage(3, LogLevel.Error, "Installing the hotkey event handler failed (OSStatus {Status})")]
        public static partial void InstallFailed(ILogger logger, int status);
    }
}
