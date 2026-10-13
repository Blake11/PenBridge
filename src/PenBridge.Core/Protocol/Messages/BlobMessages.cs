namespace PenBridge.Core.Protocol;

/// <summary>0x11 S → C: J-PAKE round 1. Payload: <c>u16 len ‖ bytes</c>, opaque here (defined by the Security layer).</summary>
public readonly struct ServerPairRound1Message : IControlMessage<ServerPairRound1Message>
{
    public ServerPairRound1Message(ReadOnlyMemory<byte> round1)
    {
        _ = WireSizes.BlobSize(round1, nameof(round1));
        Round1 = round1;
    }

    public static ControlMessageType Type => ControlMessageType.ServerPairRound1;

    public ReadOnlyMemory<byte> Round1 { get; }

    public int PayloadSize => WireSizes.BlobSize(Round1, nameof(Round1));

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteBlob(Round1.Span);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ServerPairRound1Message message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadBlob(out ReadOnlyMemory<byte> value))
        {
            return false;
        }

        message = new ServerPairRound1Message(value);
        return true;
    }
}

/// <summary>0x12 C → S: J-PAKE round 2. Payload: <c>u16 len ‖ bytes</c>, opaque here (defined by the Security layer).</summary>
public readonly struct ClientPairRound2Message : IControlMessage<ClientPairRound2Message>
{
    public ClientPairRound2Message(ReadOnlyMemory<byte> round2)
    {
        _ = WireSizes.BlobSize(round2, nameof(round2));
        Round2 = round2;
    }

    public static ControlMessageType Type => ControlMessageType.ClientPairRound2;

    public ReadOnlyMemory<byte> Round2 { get; }

    public int PayloadSize => WireSizes.BlobSize(Round2, nameof(Round2));

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteBlob(Round2.Span);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ClientPairRound2Message message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadBlob(out ReadOnlyMemory<byte> value))
        {
            return false;
        }

        message = new ClientPairRound2Message(value);
        return true;
    }
}

/// <summary>0x13 S → C: J-PAKE round 2. Payload: <c>u16 len ‖ bytes</c>, opaque here (defined by the Security layer).</summary>
public readonly struct ServerPairRound2Message : IControlMessage<ServerPairRound2Message>
{
    public ServerPairRound2Message(ReadOnlyMemory<byte> round2)
    {
        _ = WireSizes.BlobSize(round2, nameof(round2));
        Round2 = round2;
    }

    public static ControlMessageType Type => ControlMessageType.ServerPairRound2;

    public ReadOnlyMemory<byte> Round2 { get; }

    public int PayloadSize => WireSizes.BlobSize(Round2, nameof(Round2));

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteBlob(Round2.Span);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ServerPairRound2Message message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadBlob(out ReadOnlyMemory<byte> value))
        {
            return false;
        }

        message = new ServerPairRound2Message(value);
        return true;
    }
}

/// <summary>0x14 C → S: key-confirmation MAC over (transcript, Mac cert fingerprint as the phone sees it). Payload: <c>u16 len ‖ bytes</c>, opaque here (defined by the Security layer).</summary>
public readonly struct ClientPairConfirmMessage : IControlMessage<ClientPairConfirmMessage>
{
    public ClientPairConfirmMessage(ReadOnlyMemory<byte> mac)
    {
        _ = WireSizes.BlobSize(mac, nameof(mac));
        Mac = mac;
    }

    public static ControlMessageType Type => ControlMessageType.ClientPairConfirm;

    public ReadOnlyMemory<byte> Mac { get; }

    public int PayloadSize => WireSizes.BlobSize(Mac, nameof(Mac));

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteBlob(Mac.Span);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ClientPairConfirmMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadBlob(out ReadOnlyMemory<byte> value))
        {
            return false;
        }

        message = new ClientPairConfirmMessage(value);
        return true;
    }
}

/// <summary>0x15 S → C: key-confirmation MAC, same construction, server role. Payload: <c>u16 len ‖ bytes</c>, opaque here (defined by the Security layer).</summary>
public readonly struct ServerPairConfirmMessage : IControlMessage<ServerPairConfirmMessage>
{
    public ServerPairConfirmMessage(ReadOnlyMemory<byte> mac)
    {
        _ = WireSizes.BlobSize(mac, nameof(mac));
        Mac = mac;
    }

    public static ControlMessageType Type => ControlMessageType.ServerPairConfirm;

    public ReadOnlyMemory<byte> Mac { get; }

    public int PayloadSize => WireSizes.BlobSize(Mac, nameof(Mac));

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteBlob(Mac.Span);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ServerPairConfirmMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadBlob(out ReadOnlyMemory<byte> value))
        {
            return false;
        }

        message = new ServerPairConfirmMessage(value);
        return true;
    }
}
