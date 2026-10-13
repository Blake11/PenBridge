namespace PenBridge.Core.Tests;

public class ProtocolInfoTests
{
    [Fact]
    public void Constants_match_wire_contract()
    {
        Assert.Equal(1, ProtocolInfo.Version);
        Assert.Equal("_penbridge._tcp", ProtocolInfo.ServiceType);
    }
}
