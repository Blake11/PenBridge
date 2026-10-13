using PenBridge.Core.Protocol;

namespace PenBridge.Core;

/// <summary>Wire-protocol identity shared by the Mac server and the phone clients.</summary>
public static class ProtocolInfo
{
    /// <summary>
    /// Protocol version spoken by this build. A different major is refused with
    /// <see cref="ProtocolErrorCode.IncompatibleVersion"/>; a different minor is accepted and
    /// features are gated by capability flags.
    /// </summary>
    public static ProtocolVersion Version => ProtocolVersion.Current;

    /// <summary>Bonjour/mDNS service type advertised by the Mac server.</summary>
    public const string ServiceType = "_penbridge._tcp";

    /// <summary>Default TCP (control) and UDP (input) port; the server falls back to 47821–47829 (ADR-0011).</summary>
    public const int DefaultPort = 47820;
}
