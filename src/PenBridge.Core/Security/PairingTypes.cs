namespace PenBridge.Core.Security;

/// <summary>Which shared secret a pairing attempt uses (wire value of <c>secretKind</c>).</summary>
public enum PairingSecretKind : byte
{
    /// <summary>The 6-digit PIN shown on the Mac (manual or discovered path).</summary>
    Pin = 1,

    /// <summary>The 32-byte one-time token from the QR code.</summary>
    QrToken = 2,
}

/// <summary>Role of a <see cref="PairingSession"/>.</summary>
public enum PairingRole : byte
{
    /// <summary>The phone.</summary>
    Client = 1,

    /// <summary>The Mac.</summary>
    Server = 2,
}

/// <summary>Result of <see cref="PinIssuer.TryBeginAttempt"/>.</summary>
public enum PairingAttemptStart
{
    /// <summary>The attempt started; the caller owns the returned <see cref="PinAttempt"/>.</summary>
    Started,

    /// <summary>Another attempt is in progress (only one at a time).</summary>
    Busy,

    /// <summary>The pairing window expired during this call and is now closed. Maps to <c>PairResult(expired)</c>.</summary>
    Expired,

    /// <summary>No pairing window is open ("Pair new device…" not shown). No strike. Maps to <c>PairResult(expired)</c>.</summary>
    WindowClosed,

    /// <summary>Lockout backoff is active (see <see cref="PinIssuer.LockedUntil"/>). No strike. Maps to <c>PairResult(failed(0))</c>.</summary>
    LockedOut,
}

/// <summary>Outcome of a finished attempt, mapping to <c>PairResult</c>.</summary>
public enum PairingOutcome
{
    /// <summary>Paired. The secrets were consumed and regenerated.</summary>
    Ok,

    /// <summary>Failed; see <see cref="PairingResult.AttemptsLeft"/>. At 0 the PIN was replaced and backoff started.</summary>
    Failed,

    /// <summary>The window expired, closed or regenerated while the attempt ran. No strike.</summary>
    Expired,
}

/// <summary>Outcome plus the strikes left on the current PIN.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="AttemptsLeft">Attempts left before the PIN is replaced (meaningful for <see cref="PairingOutcome.Failed"/>).</param>
public readonly record struct PairingResult(PairingOutcome Outcome, int AttemptsLeft);
