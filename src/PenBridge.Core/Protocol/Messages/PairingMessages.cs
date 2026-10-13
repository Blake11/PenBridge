namespace PenBridge.Core.Protocol;

/// <summary>
/// 0x10 C → S. Payload: <c>u16 len ‖ J-PAKE round-1 bytes ‖ u8 secretKind</c>.
/// The round-1 bytes are opaque here; the Security layer defines them.
/// </summary>
public readonly struct ClientPairRound1Message : IControlMessage<ClientPairRound1Message>
{
    public ClientPairRound1Message(ReadOnlyMemory<byte> round1, PairingSecretKind secretKind)
    {
        _ = WireSizes.BlobSize(round1, nameof(round1));
        if (!IsDefined(secretKind))
        {
            throw new ArgumentOutOfRangeException(nameof(secretKind));
        }

        Round1 = round1;
        SecretKind = secretKind;
    }

    public static ControlMessageType Type => ControlMessageType.ClientPairRound1;

    public ReadOnlyMemory<byte> Round1 { get; }

    public PairingSecretKind SecretKind { get; }

    public int PayloadSize
    {
        get
        {
            if (!IsDefined(SecretKind))
            {
                throw new InvalidOperationException("Secret kind is not set.");
            }

            return WireSizes.BlobSize(Round1, nameof(Round1)) + 1;
        }
    }

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteBlob(Round1.Span);
        w.WriteU8((byte)SecretKind);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ClientPairRound1Message message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadBlob(out ReadOnlyMemory<byte> round1) || !r.TryReadU8(out byte kind) || !IsDefined((PairingSecretKind)kind))
        {
            return false;
        }

        message = new ClientPairRound1Message(round1, (PairingSecretKind)kind);
        return true;
    }

    private static bool IsDefined(PairingSecretKind kind) => kind is PairingSecretKind.Pin or PairingSecretKind.QrToken;
}

/// <summary>0x16 S → C. Payload: <c>u8 status ‖ u8 attemptsLeft</c> (attemptsLeft is 0 unless status is Failed).</summary>
public readonly struct PairResultMessage : IControlMessage<PairResultMessage>
{
    private PairResultMessage(PairResultStatus status, byte attemptsLeft)
    {
        Status = status;
        AttemptsLeft = attemptsLeft;
    }

    public static ControlMessageType Type => ControlMessageType.PairResult;

    public PairResultStatus Status { get; }

    /// <summary>Remaining attempts with the current PIN; meaningful only for <see cref="PairResultStatus.Failed"/>.</summary>
    public byte AttemptsLeft { get; }

    public int PayloadSize => 2;

    public static PairResultMessage Ok() => new(PairResultStatus.Ok, 0);

    public static PairResultMessage Failed(byte attemptsLeft) => new(PairResultStatus.Failed, attemptsLeft);

    public static PairResultMessage Expired() => new(PairResultStatus.Expired, 0);

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        destination[0] = (byte)Status;
        destination[1] = AttemptsLeft;
        return 2;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out PairResultMessage message)
    {
        message = default;
        if (payload.Length < 2)
        {
            return false;
        }

        var status = (PairResultStatus)payload[0];
        switch (status)
        {
            case PairResultStatus.Ok:
            case PairResultStatus.Expired:
                message = new PairResultMessage(status, 0);
                return true;
            case PairResultStatus.Failed:
                message = new PairResultMessage(status, payload[1]);
                return true;
            default:
                return false;
        }
    }
}
