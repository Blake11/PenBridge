using System.Buffers.Text;
using System.Security.Cryptography;

namespace PenBridge.Core.Security;

/// <summary>
/// Server-side issuer and policy for pairing secrets (ADR-0009): a 6-digit PIN and a 32-byte QR
/// token, both from a CSPRNG and sharing one lifetime and one strike counter.
/// </summary>
/// <remarks>
/// <para><b>Policy</b></para>
/// <list type="bullet">
/// <item><b>Pairing window:</b> attempts are accepted only between <see cref="OpenPairingWindow"/>
/// (the Mac's "Pair new device…") and its close. The window closes on success, on
/// <see cref="ClosePairingWindow"/>, or <see cref="Lifetime"/> after the current secrets were issued.
/// Attempts outside the window are refused without a strike.</item>
/// <item><b>Strikes:</b> after <see cref="MaxFailedAttempts"/> failures the secrets are replaced (a new
/// PIN is shown, with a fresh lifetime) and a lockout backoff starts: 30 s, doubling per lockout up to
/// 10 min. The backoff counter resets only on a successful pairing (not on window close).</item>
/// <item><b>One-time:</b> a success consumes both secrets and closes the window.</item>
/// <item><b>One at a time:</b> a second attempt gets <see cref="PairingAttemptStart.Busy"/>. A
/// <see cref="PinAttempt"/> disposed without <see cref="PinAttempt.Complete"/> counts as a failure.</item>
/// <item><b>Late completion:</b> an attempt that completes at or after expiry, or after the secrets
/// were replaced, returns <see cref="PairingOutcome.Expired"/>.</item>
/// </list>
/// <para><b>Ordering contract:</b> the server must obtain <see cref="PairingOutcome.Ok"/> from
/// <see cref="PinAttempt.Complete"/> <i>before</i> it sends its own confirmation. A server
/// <see cref="PairingSession"/> created with <see cref="PairingSession.ForServer"/> enforces this.</para>
/// <para><b>Security notes:</b> secrets are never compared directly; they only feed J-PAKE. Byte copies
/// are zeroed on completion. Known gaps: the display strings (<see cref="CurrentPin"/>,
/// <see cref="CurrentQrTokenBase64Url"/>) are immutable .NET strings and cannot be zeroed; BouncyCastle
/// keeps its own copy of the password chars and derives immutable <c>BigInteger</c> values (the secret
/// scalar and the keying material) that cannot be zeroed and live until collected. Strike and backoff
/// state is in memory only, so restarting the server resets it.</para>
/// <para>Thread-safe. No timers: expiry is evaluated lazily and <see cref="SecretsChanged"/> fires on
/// the calling thread (the UI can schedule a refresh for <see cref="ExpiresAt"/>).</para>
/// </remarks>
public sealed class PinIssuer
{
    /// <summary>How long a PIN/token set (and so the pairing window) stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>Backoff after the first lockout; doubles per further lockout.</summary>
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Backoff cap.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

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
    private string? _pinText;
    private string? _tokenText;
    private bool _windowOpen;
    private DateTimeOffset _expiresAt;
    private DateTimeOffset _lockedUntil;
    private int _failures;
    private int _lockouts;
    private long _generation;
    private PinAttempt? _active;

    /// <summary>Creates an issuer with the pairing window closed.</summary>
    public PinIssuer(TimeProvider? timeProvider = null)
        : this(timeProvider ?? TimeProvider.System, static () => RandomNumberGenerator.GetInt32(0, 1_000_000))
    {
    }

    internal PinIssuer(TimeProvider timeProvider, Func<int> nextPin)
    {
        _time = timeProvider;
        _nextPin = nextPin;
    }

    /// <summary>Raised (outside the lock) after the window opens or closes or the secrets are replaced.</summary>
    public event EventHandler? SecretsChanged;

    /// <summary>Whether a pairing window is open (evaluates expiry).</summary>
    public bool IsWindowOpen
    {
        get
        {
            bool changed;
            bool open;
            lock (_lock)
            {
                changed = CloseIfExpiredLocked();
                open = _windowOpen;
            }

            RaiseIf(changed);
            return open;
        }
    }

    /// <summary>The PIN to display, or <see langword="null"/> when no window is open.</summary>
    public string? CurrentPin
    {
        get
        {
            bool changed;
            string? pin;
            lock (_lock)
            {
                changed = CloseIfExpiredLocked();
                pin = _windowOpen ? _pinText : null;
            }

            RaiseIf(changed);
            return pin;
        }
    }

    /// <summary>The QR token (unpadded base64url), or <see langword="null"/> when no window is open.</summary>
    public string? CurrentQrTokenBase64Url
    {
        get
        {
            bool changed;
            string? token;
            lock (_lock)
            {
                changed = CloseIfExpiredLocked();
                token = _windowOpen ? _tokenText : null;
            }

            RaiseIf(changed);
            return token;
        }
    }

    /// <summary>When the current secrets (and the window) expire.</summary>
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

    /// <summary>Attempts are refused until this instant after a lockout (in the past when none is active).</summary>
    public DateTimeOffset LockedUntil
    {
        get
        {
            lock (_lock)
            {
                return _lockedUntil;
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

    /// <summary>Opens (or restarts) the pairing window with fresh secrets valid for <see cref="Lifetime"/>.</summary>
    public void OpenPairingWindow()
    {
        lock (_lock)
        {
            RegenerateLocked();
            _windowOpen = true;
        }

        RaiseIf(true);
    }

    /// <summary>Closes the window and invalidates the secrets; an in-flight attempt completes as Expired.</summary>
    public void ClosePairingWindow()
    {
        bool changed;
        lock (_lock)
        {
            changed = _windowOpen;
            CloseLocked();
        }

        RaiseIf(changed);
    }

    /// <summary>
    /// Starts a pairing attempt using the secret of <paramref name="kind"/>. On
    /// <see cref="PairingAttemptStart.Started"/> the caller owns <paramref name="attempt"/> and must
    /// complete or dispose it (or hand it to <see cref="PairingSession.ForServer"/>, which does).
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
            changed = CloseIfExpiredLocked();
            if (changed)
            {
                result = PairingAttemptStart.Expired;
            }
            else if (!_windowOpen)
            {
                result = PairingAttemptStart.WindowClosed;
            }
            else if (_time.GetUtcNow() < _lockedUntil)
            {
                result = PairingAttemptStart.LockedOut;
            }
            else if (_active is not null)
            {
                result = PairingAttemptStart.Busy;
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

            DateTimeOffset now = _time.GetUtcNow();
            if (attempt.Generation != _generation || !_windowOpen)
            {
                result = new PairingResult(PairingOutcome.Expired, MaxFailedAttempts - _failures);
            }
            else if (now >= _expiresAt)
            {
                CloseLocked();
                changed = true;
                result = new PairingResult(PairingOutcome.Expired, 0);
            }
            else if (success)
            {
                _lockouts = 0;
                _lockedUntil = default;
                CloseLocked();
                changed = true;
                result = new PairingResult(PairingOutcome.Ok, MaxFailedAttempts);
            }
            else
            {
                _failures++;
                int left = MaxFailedAttempts - _failures;
                if (left <= 0)
                {
                    _lockedUntil = now + BackoffFor(_lockouts);
                    _lockouts++;
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

    private static TimeSpan BackoffFor(int previousLockouts)
    {
        int shift = Math.Min(previousLockouts, 10);
        TimeSpan backoff = InitialBackoff * (1 << shift);
        return backoff < MaxBackoff ? backoff : MaxBackoff;
    }

    private bool CloseIfExpiredLocked()
    {
        if (!_windowOpen || _time.GetUtcNow() < _expiresAt)
        {
            return false;
        }

        CloseLocked();
        return true;
    }

    private void CloseLocked()
    {
        _windowOpen = false;
        CryptographicOperations.ZeroMemory(_pin);
        CryptographicOperations.ZeroMemory(_token);
        _pinText = null;
        _tokenText = null;
        _failures = 0;
        _generation++;
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
/// One in-flight pairing attempt. Prefer handing it to <see cref="PairingSession.ForServer"/>, which
/// completes it at key confirmation; otherwise call <see cref="Complete"/> yourself.
/// </summary>
public sealed class PinAttempt : IDisposable
{
    private readonly PinIssuer _issuer;
    private readonly byte[] _secret;

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

    /// <summary>The recorded result, or <see langword="null"/> while the attempt is open.</summary>
    public PairingResult? Result { get; private set; }

    internal long Generation { get; }

    /// <summary>Records the outcome (idempotent: later calls return the first result).</summary>
    public PairingResult Complete(bool success)
    {
        if (Result is { } done)
        {
            return done;
        }

        CryptographicOperations.ZeroMemory(_secret);
        PairingResult result = _issuer.Finish(this, success);
        Result = result;
        return result;
    }

    /// <summary>Completes as a failure if <see cref="Complete"/> was not called.</summary>
    public void Dispose() => Complete(false);
}
