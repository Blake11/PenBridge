namespace PenBridge.Core.Protocol;

/// <summary>0x20 C → S. Payload: <c>deviceId(16) ‖ hmac(32) ‖ clientNonce(32)</c>.</summary>
public readonly struct AuthMessage : IControlMessage<AuthMessage>
{
    public AuthMessage(ReadOnlyMemory<byte> deviceId, ReadOnlyMemory<byte> hmac, ReadOnlyMemory<byte> clientNonce)
    {
        WireSizes.RequireLength(deviceId, WireSizes.DeviceId, nameof(deviceId));
        WireSizes.RequireLength(hmac, WireSizes.AuthMac, nameof(hmac));
        WireSizes.RequireLength(clientNonce, WireSizes.Nonce, nameof(clientNonce));
        DeviceId = deviceId;
        Hmac = hmac;
        ClientNonce = clientNonce;
    }

    public static ControlMessageType Type => ControlMessageType.Auth;

    public const int Size = WireSizes.DeviceId + WireSizes.AuthMac + WireSizes.Nonce;

    public ReadOnlyMemory<byte> DeviceId { get; }

    /// <summary><c>HMAC-SHA256(K_dev, "pb-auth-v1" ‖ challenge ‖ clientNonce ‖ certFingerprint)</c>.</summary>
    public ReadOnlyMemory<byte> Hmac { get; }

    public ReadOnlyMemory<byte> ClientNonce { get; }

    public int PayloadSize
    {
        get
        {
            WireSizes.RequireLength(DeviceId, WireSizes.DeviceId, nameof(DeviceId));
            WireSizes.RequireLength(Hmac, WireSizes.AuthMac, nameof(Hmac));
            WireSizes.RequireLength(ClientNonce, WireSizes.Nonce, nameof(ClientNonce));
            return Size;
        }
    }

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteBytes(DeviceId.Span);
        w.WriteBytes(Hmac.Span);
        w.WriteBytes(ClientNonce.Span);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out AuthMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadFixed(WireSizes.DeviceId, out ReadOnlyMemory<byte> deviceId)
            || !r.TryReadFixed(WireSizes.AuthMac, out ReadOnlyMemory<byte> hmac)
            || !r.TryReadFixed(WireSizes.Nonce, out ReadOnlyMemory<byte> nonce))
        {
            return false;
        }

        message = new AuthMessage(deviceId, hmac, nonce);
        return true;
    }
}

/// <summary>0x21 S → C. Payload: <c>u16 udpPort ‖ u32 sessionId ‖ serverNonce(32)</c>.</summary>
public readonly struct SessionStartMessage : IControlMessage<SessionStartMessage>
{
    public SessionStartMessage(ushort udpPort, uint sessionId, ReadOnlyMemory<byte> serverNonce)
    {
        WireSizes.RequireLength(serverNonce, WireSizes.Nonce, nameof(serverNonce));
        UdpPort = udpPort;
        SessionId = sessionId;
        ServerNonce = serverNonce;
    }

    public static ControlMessageType Type => ControlMessageType.SessionStart;

    public const int Size = 2 + 4 + WireSizes.Nonce;

    public ushort UdpPort { get; }

    public uint SessionId { get; }

    public ReadOnlyMemory<byte> ServerNonce { get; }

    public int PayloadSize
    {
        get
        {
            WireSizes.RequireLength(ServerNonce, WireSizes.Nonce, nameof(ServerNonce));
            return Size;
        }
    }

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU16(UdpPort);
        w.WriteU32(SessionId);
        w.WriteBytes(ServerNonce.Span);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out SessionStartMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU16(out ushort port) || port == 0
            || !r.TryReadU32(out uint sessionId)
            || !r.TryReadFixed(WireSizes.Nonce, out ReadOnlyMemory<byte> nonce))
        {
            return false;
        }

        message = new SessionStartMessage(port, sessionId, nonce);
        return true;
    }
}

/// <summary>
/// 0x22 C → S. Payload: <c>u16 widthPx ‖ u16 heightPx ‖ f32 xdpi ‖ f32 ydpi ‖ u8 orientation ‖ u32 capabilities</c>.
/// Sizes must be non-zero and DPIs finite and positive.
/// </summary>
public readonly struct SurfaceInfoMessage : IControlMessage<SurfaceInfoMessage>
{
    public SurfaceInfoMessage(ushort widthPx, ushort heightPx, float xDpi, float yDpi, SurfaceOrientation orientation, SurfaceCapabilities capabilities)
    {
        if (!IsValid(widthPx, heightPx, xDpi, yDpi, orientation))
        {
            throw new ArgumentException("Invalid surface description.");
        }

        WidthPx = widthPx;
        HeightPx = heightPx;
        XDpi = xDpi;
        YDpi = yDpi;
        Orientation = orientation;
        Capabilities = capabilities;
    }

    public static ControlMessageType Type => ControlMessageType.SurfaceInfo;

    public const int Size = 2 + 2 + 4 + 4 + 1 + 4;

    public ushort WidthPx { get; }

    public ushort HeightPx { get; }

    public float XDpi { get; }

    public float YDpi { get; }

    public SurfaceOrientation Orientation { get; }

    public SurfaceCapabilities Capabilities { get; }

    public int PayloadSize => IsValid(WidthPx, HeightPx, XDpi, YDpi, Orientation)
        ? Size
        : throw new InvalidOperationException("Invalid surface description.");

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU16(WidthPx);
        w.WriteU16(HeightPx);
        w.WriteF32(XDpi);
        w.WriteF32(YDpi);
        w.WriteU8((byte)Orientation);
        w.WriteU32((uint)Capabilities);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out SurfaceInfoMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU16(out ushort width) || !r.TryReadU16(out ushort height)
            || !r.TryReadF32(out float xDpi) || !r.TryReadF32(out float yDpi)
            || !r.TryReadU8(out byte orientation) || !r.TryReadU32(out uint caps)
            || !IsValid(width, height, xDpi, yDpi, (SurfaceOrientation)orientation))
        {
            return false;
        }

        message = new SurfaceInfoMessage(width, height, xDpi, yDpi, (SurfaceOrientation)orientation, (SurfaceCapabilities)caps);
        return true;
    }

    private static bool IsValid(ushort width, ushort height, float xDpi, float yDpi, SurfaceOrientation orientation) =>
        width > 0 && height > 0
        && float.IsFinite(xDpi) && xDpi > 0
        && float.IsFinite(yDpi) && yDpi > 0
        && orientation is SurfaceOrientation.Portrait or SurfaceOrientation.Landscape;
}

/// <summary>0x23 S → C. Payload: <c>u64 highestSequence</c>.</summary>
public readonly struct UdpAckMessage(ulong highestSequence) : IControlMessage<UdpAckMessage>
{
    public static ControlMessageType Type => ControlMessageType.UdpAck;

    public ulong HighestSequence { get; } = highestSequence;

    public int PayloadSize => 8;

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU64(HighestSequence);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out UdpAckMessage message)
    {
        var r = new WireReader(payload);
        bool ok = r.TryReadU64(out ulong value);
        message = new UdpAckMessage(value);
        return ok;
    }
}

/// <summary>
/// 0x24 C → S, TLS fallback mode only. Payload: an <see cref="InputPayload"/> exactly as in a decrypted datagram.
/// Allocation-free to write and read.
/// </summary>
public readonly struct InputFrameMessage(InputPayload payload) : IControlMessage<InputFrameMessage>
{
    public static ControlMessageType Type => ControlMessageType.InputFrame;

    public InputPayload Payload { get; } = payload;

    public int PayloadSize => Payload.Size;

    public int WritePayload(Span<byte> destination) => Payload.Write(destination);

    public static bool TryRead(ReadOnlySpan<byte> payload, out InputFrameMessage message)
    {
        bool ok = InputPayload.TryRead(payload, out InputPayload decoded);
        message = new InputFrameMessage(decoded);
        return ok;
    }
}

/// <summary>0x30 both. Payload: <c>u64 timestampUs</c> (sender clock, echoed in <see cref="PongMessage"/>).</summary>
public readonly struct PingMessage(ulong timestampUs) : IControlMessage<PingMessage>
{
    public static ControlMessageType Type => ControlMessageType.Ping;

    public ulong TimestampUs { get; } = timestampUs;

    public int PayloadSize => 8;

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU64(TimestampUs);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out PingMessage message)
    {
        var r = new WireReader(payload);
        bool ok = r.TryReadU64(out ulong value);
        message = new PingMessage(value);
        return ok;
    }
}

/// <summary>0x31 both. Payload: <c>u64 timestampUs</c>.</summary>
public readonly struct PongMessage(ulong timestampUs) : IControlMessage<PongMessage>
{
    public static ControlMessageType Type => ControlMessageType.Pong;

    public ulong TimestampUs { get; } = timestampUs;

    public int PayloadSize => 8;

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU64(TimestampUs);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out PongMessage message)
    {
        var r = new WireReader(payload);
        bool ok = r.TryReadU64(out ulong value);
        message = new PongMessage(value);
        return ok;
    }
}

/// <summary>0x7E S → C. Empty payload: another session is active and takeover is off.</summary>
public readonly struct BusyMessage : IControlMessage<BusyMessage>
{
    public static ControlMessageType Type => ControlMessageType.Busy;

    public int PayloadSize => 0;

    public int WritePayload(Span<byte> destination) => 0;

    public static bool TryRead(ReadOnlySpan<byte> payload, out BusyMessage message)
    {
        message = default;
        return true;
    }
}

/// <summary>0x7F both. Payload: <c>u16 code ‖ string message</c>. Unknown codes are accepted.</summary>
public readonly struct ErrorMessage : IControlMessage<ErrorMessage>
{
    public ErrorMessage(ProtocolErrorCode code, string message)
    {
        _ = WireString.GetSize(message, nameof(message));
        Code = code;
        Message = message;
    }

    public static ControlMessageType Type => ControlMessageType.Error;

    public ProtocolErrorCode Code { get; }

    /// <summary>Human-readable detail. Never put keys, PINs or tokens here.</summary>
    public string Message { get; }

    public int PayloadSize => 2 + WireString.GetSize(Message, nameof(Message));

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU16((ushort)Code);
        w.WriteString(Message);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ErrorMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU16(out ushort code) || !r.TryReadString(out string text))
        {
            return false;
        }

        message = new ErrorMessage((ProtocolErrorCode)code, text);
        return true;
    }
}

/// <summary>0x70 both. Payload: <c>u8 reason</c>. Unknown reasons are accepted.</summary>
public readonly struct ByeMessage(ByeReason reason) : IControlMessage<ByeMessage>
{
    public static ControlMessageType Type => ControlMessageType.Bye;

    public ByeReason Reason { get; } = reason;

    public int PayloadSize => 1;

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        destination[0] = (byte)Reason;
        return 1;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ByeMessage message)
    {
        message = default;
        if (payload.IsEmpty)
        {
            return false;
        }

        message = new ByeMessage((ByeReason)payload[0]);
        return true;
    }
}
