using System.Buffers.Text;
using System.Security.Cryptography;

namespace PenBridge.Core.Security;

/// <summary>
/// Server-side issuer and policy for pairing secrets (ADR-0009): a 6-digit PIN and a 32-byte QR
/// token, both from a CSPRNG and sharing one lifetime and one strike counter.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Valid for <see cref="Lifetime"/>; an expired set is replaced lazily on the next access.</item>
/// <item>After <see cref="MaxFailedAttempts"/> failures the set is replaced (a new PIN is shown).</item>
/// <item>A successful pairing consumes the set (both secrets are one-time).</item>
/// <item>Only one attempt runs at a time; a <see cref="PinAttempt"/> disposed without
/// <see cref="PinAttempt.Complete"/> counts as a failure, so aborting mid-protocol costs a strike.</item>
/// </list>
/// The secrets never leave this type except through <see cref="CurrentPin"/>/<see cref="CurrentQrTokenBase64Url"/>
/// (for display) and <see cref="PinAttempt.Secret"/> (fed into J-PAKE, never compared directly).
/// Thread-safe. Idle cost is zero: no timers, <see cref="SecretsChanged"/> fires from the calling thread.
/// </remarks>
public sealed class PinIssuer
{
    /// <summary>How long a PIN/token set stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>Failures allowed before the set is replaced.</summary>
    public const int MaxFailedAttempts = 3;

    /// <summary>Number of PIN digits.</summary>
    public const int PinLength = 6;

    /// <summary>QR token length in bytes.</summary>
    public const int QrTokenLength = 32;

    private readonly TimeProvider _time;
    private readonly Func<int> _nextPin;
    private readonly Lock _lock = new();
    private readonly byte[] _pin = new byte[PinLength];
    private readonly byte[] _token = new byte[QrTokenLength];
    private string _pinText = "";
    private string _tokenText = "";
    private DateTimeOffset _expiresAt;
    private int _failures;
    private long _generation;
    private PinAttempt? _active;

    /// <summary>Creates an issuer with a fresh secret set.</summary>
    public PinIssuer(TimeProvider? timeProvider = null)
        : this(timeProvider ?? TimeProvider.System, static () => RandomNumberGenerator.GetInt32(0, 1_000_000))
    {
    }

    internal PinIssuer(TimeProvider timeProvider, Func<int> nextPin)
    {
        _time = timeProvider;
        _nextPin = nextPin;
        lock (_lock)
        {
            RegenerateLocked();
        }
    }

    /// <summary>Raised (outside the lock) after the secret set is replaced.</summary>
    public event EventHandler? SecretsChanged;

    /// <summary>The PIN to display; replaced first if expired.</summary>
    public string CurrentPin
    {
        get
        {
            bool changed;
            string pin;
            lock (_lock)
            {
                changed = RefreshIfExpiredLocked();
                pin = _pinText;
            }

            RaiseIf(changed);
            return pin;
        }
    }

    /// <summary>The QR token (unpadded base64url) for the QR payload; replaced first if expired.</summary>
    public string CurrentQrTokenBase64Url
    {
        get
        {
            bool changed;
            string token;
            lock (_lock)
            {
                changed = RefreshIfExpiredLocked();
                token = _tokenText;
            }

            RaiseIf(changed);
            return token;
        }
    }

    /// <summary>When the current set expires (the UI can schedule a refresh for this instant).</summary>
    public DateTimeOffset ExpiresAt
    {
        get
        {
            lock (_lock)
            {
                return _expiresAt;
            }
        }
    }

    /// <summary>Attempts left on the current set.</summary>
    public int AttemptsLeft
    {
        get
        {
            lock (_lock)
            {
                return MaxFailedAttempts - _failures;
            }
        }
    }

    /// <summary>Discards the current set (e.g. the pairing window was reopened) and makes a new one.</summary>
    public void Regenerate()
    {
        lock (_lock)
        {
            RegenerateLocked();
        }

        RaiseIf(true);
    }

    /// <summary>
    /// Starts a pairing attempt using the secret of <paramref name="kind"/>. On
    /// <see cref="PairingAttemptStart.Started"/> the caller owns <paramref name="attempt"/> and must
    /// complete or dispose it.
    /// </summary>
    public PairingAttemptStart TryBeginAttempt(PairingSecretKind kind, out PinAttempt? attempt)
    {
        if (kind is not (PairingSecretKind.Pin or PairingSecretKind.QrToken))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        attempt = null;
        bool changed;
        PairingAttemptStart result;
        lock (_lock)
        {
            if (_active is not null)
            {
                return PairingAttemptStart.Busy;
            }

            changed = RefreshIfExpiredLocked();
            if (changed)
            {
                result = PairingAttemptStart.Expired;
            }
            else
            {
                byte[] secret = kind == PairingSecretKind.Pin ? (byte[])_pin.Clone() : (byte[])_token.Clone();
                attempt = new PinAttempt(this, kind, secret, _generation);
                _active = attempt;
                result = PairingAttemptStart.Started;
            }
        }

        RaiseIf(changed);
        return result;
    }

    internal PairingResult Finish(PinAttempt attempt, bool success)
    {
        PairingResult result;
        bool changed = false;
        lock (_lock)
        {
            if (ReferenceEquals(_active, attempt))
            {
                _active = null;
            }

            if (attempt.Generation != _generation)
            {
                result = new PairingResult(PairingOutcome.Expired, MaxFailedAttempts - _failures);
            }
            else if (success)
            {
                RegenerateLocked();
                changed = true;
                result = new PairingResult(PairingOutcome.Ok, MaxFailedAttempts);
            }
            else
            {
                _failures++;
                int left = MaxFailedAttempts - _failures;
                if (left <= 0)
                {
                    RegenerateLocked();
                    changed = true;
                    left = 0;
                }

                result = new PairingResult(PairingOutcome.Failed, left);
            }
        }

        RaiseIf(changed);
        return result;
    }

    private bool RefreshIfExpiredLocked()
    {
        if (_time.GetUtcNow() < _expiresAt)
        {
            return false;
        }

        RegenerateLocked();
        return true;
    }

    private void RegenerateLocked()
    {
        int value = _nextPin();
        if ((uint)value >= 1_000_000)
        {
            throw new InvalidOperationException("PIN source out of range.");
        }

        for (int i = PinLength - 1; i >= 0; i--)
        {
            _pin[i] = (byte)('0' + (value % 10));
            value /= 10;
        }

        RandomNumberGenerator.Fill(_token);
        _pinText = System.Text.Encoding.ASCII.GetString(_pin);
        _tokenText = Base64Url.EncodeToString(_token);
        _expiresAt = _time.GetUtcNow() + Lifetime;
        _failures = 0;
        _generation++;
    }

    private void RaiseIf(bool changed)
    {
        if (changed)
        {
            SecretsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// One in-flight pairing attempt. Feed <see cref="Secret"/> to a server <see cref="PairingSession"/>,
/// then call <see cref="Complete"/> with whether key confirmation succeeded.
/// </summary>
public sealed class PinAttempt : IDisposable
{
    private readonly PinIssuer _issuer;
    private readonly byte[] _secret;
    private PairingResult? _result;

    internal PinAttempt(PinIssuer issuer, PairingSecretKind kind, byte[] secret, long generation)
    {
        _issuer = issuer;
        Kind = kind;
        _secret = secret;
        Generation = generation;
    }

    /// <summary>The secret kind the client asked for.</summary>
    public PairingSecretKind Kind { get; }

    /// <summary>The secret bytes: ASCII PIN digits, or the raw 32-byte token. Zeroed on completion.</summary>
    public ReadOnlySpan<byte> Secret => _secret;

    internal long Generation { get; }

    /// <summary>Records the outcome (idempotent: later calls return the first result).</summary>
    public PairingResult Complete(bool success)
    {
        if (_result is { } done)
        {
            return done;
        }

        CryptographicOperations.ZeroMemory(_secret);
        PairingResult result = _issuer.Finish(this, success);
        _result = result;
        return result;
    }

    /// <summary>Completes as a failure if <see cref="Complete"/> was not called.</summary>
    public void Dispose() => Complete(false);
}
