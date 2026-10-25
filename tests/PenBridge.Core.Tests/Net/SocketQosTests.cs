using System.Net;
using System.Net.Sockets;
using PenBridge.Core.Net;

namespace PenBridge.Core.Tests.Net;

/// <summary>DSCP marking of the input and video sockets (screen-mirroring spec "QoS").</summary>
public class SocketQosTests
{
    [Fact]
    public void An_ipv4_socket_gets_the_tos_byte()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Assert.True(SocketQos.TrySetTos(socket, SocketQos.Video));
        Assert.Equal(SocketQos.Video, (int)socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!);
    }

    [Fact]
    public void A_dual_mode_ipv6_socket_gets_the_traffic_class()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(), "the IPV6_TCLASS number is known for macOS and Linux here");
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        Assert.True(SocketQos.TrySetTos(socket, SocketQos.Video));
        Assert.Equal(SocketQos.Video, SocketQos.GetTrafficClass(socket));
    }
}
