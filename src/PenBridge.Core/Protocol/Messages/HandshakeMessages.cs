namespace PenBridge.Core.Protocol;

/// <summary>
/// 0x01 C → S. Payload: <c>version(2) ‖ deviceId(16) ‖ string deviceName ‖ u32 capabilities</c>.
/// </summary>
public readonly struct HelloMessage : IControlMessage<HelloMessage>
{
    public HelloMessage(ProtocolVersion version, ReadOnlyMemory<byte> deviceId, string deviceName, ProtocolCapabilities capabilities)
    {
        WireSizes.RequireLength(deviceId, WireSizes.DeviceId, nameof(deviceId));
        _ = WireString.GetSize(deviceName, nameof(deviceName));
        Version = version;
        DeviceId = deviceId;
        DeviceName = deviceName;
        Capabilities = capabilities;
    }

    public static ControlMessageType Type => ControlMessageType.Hello;

    public ProtocolVersion Version { get; }

    public ReadOnlyMemory<byte> DeviceId { get; }

    public string DeviceName { get; }

    public ProtocolCapabilities Capabilities { get; }

    public int PayloadSize
    {
        get
        {
            WireSizes.RequireLength(DeviceId, WireSizes.DeviceId, nameof(DeviceId));
            return ProtocolVersion.Size + WireSizes.DeviceId + WireString.GetSize(DeviceName, nameof(DeviceName)) + 4;
        }
    }

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU8(Version.Major);
        w.WriteU8(Version.Minor);
        w.WriteBytes(DeviceId.Span);
        w.WriteString(DeviceName);
        w.WriteU32((uint)Capabilities);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out HelloMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU8(out byte major) || !r.TryReadU8(out byte minor)
            || !r.TryReadFixed(WireSizes.DeviceId, out ReadOnlyMemory<byte> deviceId)
            || !r.TryReadString(out string name)
            || !r.TryReadU32(out uint caps))
        {
            return false;
        }

        message = new HelloMessage(new ProtocolVersion(major, minor), deviceId, name, (ProtocolCapabilities)caps);
        return true;
    }
}

/// <summary>
/// 0x02 S → C. Payload: <c>version(2) ‖ string serverName ‖ u32 capabilities ‖ challenge(32)</c>.
/// </summary>
public readonly struct HelloAckMessage : IControlMessage<HelloAckMessage>
{
    public HelloAckMessage(ProtocolVersion version, string serverName, ProtocolCapabilities capabilities, ReadOnlyMemory<byte> challenge)
    {
        _ = WireString.GetSize(serverName, nameof(serverName));
        WireSizes.RequireLength(challenge, WireSizes.Challenge, nameof(challenge));
        Version = version;
        ServerName = serverName;
        Capabilities = capabilities;
        Challenge = challenge;
    }

    public static ControlMessageType Type => ControlMessageType.HelloAck;

    public ProtocolVersion Version { get; }

    public string ServerName { get; }

    public ProtocolCapabilities Capabilities { get; }

    public ReadOnlyMemory<byte> Challenge { get; }

    public int PayloadSize
    {
        get
        {
            WireSizes.RequireLength(Challenge, WireSizes.Challenge, nameof(Challenge));
            return ProtocolVersion.Size + WireString.GetSize(ServerName, nameof(ServerName)) + 4 + WireSizes.Challenge;
        }
    }

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU8(Version.Major);
        w.WriteU8(Version.Minor);
        w.WriteString(ServerName);
        w.WriteU32((uint)Capabilities);
        w.WriteBytes(Challenge.Span);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out HelloAckMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU8(out byte major) || !r.TryReadU8(out byte minor)
            || !r.TryReadString(out string name)
            || !r.TryReadU32(out uint caps)
            || !r.TryReadFixed(WireSizes.Challenge, out ReadOnlyMemory<byte> challenge))
        {
            return false;
        }

        message = new HelloAckMessage(new ProtocolVersion(major, minor), name, (ProtocolCapabilities)caps, challenge);
        return true;
    }
}
