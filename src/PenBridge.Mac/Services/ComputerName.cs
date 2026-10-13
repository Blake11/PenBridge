using System.Runtime.InteropServices;
using CoreFoundation;
using ObjCRuntime;

namespace PenBridge.Mac.Services;

/// <summary>The user-visible computer name (System Settings ▸ General ▸ Sharing), e.g. "Paul's MacBook Pro".</summary>
public static partial class ComputerName
{
    private const string SystemConfiguration = "/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration";

    /// <summary><c>SCDynamicStoreCopyComputerName</c>, falling back to the host name.</summary>
    public static string Get()
    {
        nint handle = SCDynamicStoreCopyComputerName(0, 0);
        string? name = handle == 0 ? null : CFString.FromHandle(new NativeHandle(handle), releaseHandle: true);
        return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name;
    }

    [LibraryImport(SystemConfiguration)]
    private static partial nint SCDynamicStoreCopyComputerName(nint store, nint nameEncoding);
}
