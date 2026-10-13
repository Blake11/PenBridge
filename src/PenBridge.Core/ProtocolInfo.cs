namespace PenBridge.Core;

/// <summary>Wire-protocol identity shared by the Mac server and the phone clients.</summary>
public static class ProtocolInfo
{
    /// <summary>Major protocol version; a mismatch is refused during hello.</summary>
    public const int Version = 1;

    /// <summary>Bonjour/mDNS service type advertised by the Mac server.</summary>
    public const string ServiceType = "_penbridge._tcp";
}
