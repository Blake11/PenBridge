using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests;

public class ProtocolInfoTests
{
    [Fact]
    public void Constants_match_wire_contract()
    {
        Assert.Equal(new ProtocolVersion(1, 2), ProtocolInfo.Version);
        Assert.Equal("1.2", ProtocolInfo.Version.ToString());
        Assert.Equal("_penbridge._tcp", ProtocolInfo.ServiceType);
        Assert.Equal(47820, ProtocolInfo.DefaultPort);
    }
}
