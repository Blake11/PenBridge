namespace PenBridge.Core.Net;

/// <summary>
/// Phone-side rules for retrying a connection and auto-connecting to the last-used server (Phase 3 spec, P3-A).
/// Pure functions so the platform heads stay thin and the rules are tested in Core.
/// </summary>
public static class ReconnectPolicy
{
    /// <summary>Delay before the first retry.</summary>
    public static TimeSpan InitialDelay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound of the doubling delay.</summary>
    public static TimeSpan MaxDelay { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay before retry number <paramref name="attempt"/> (0-based): 1, 2, 4, 8, 16, then 30 s for every later
    /// attempt. Negative attempts are treated as 0.
    /// </summary>
    public static TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt <= 0)
        {
            return InitialDelay;
        }

        // 2^5 s already exceeds the cap; clamping the shift also avoids overflow.
        int shift = Math.Min(attempt, 5);
        TimeSpan delay = TimeSpan.FromTicks(InitialDelay.Ticks << shift);
        return delay < MaxDelay ? delay : MaxDelay;
    }

    /// <summary>
    /// Whether a failed reconnect (with stored credentials) is worth retrying automatically. Transient network
    /// failures and <see cref="ConnectFailure.Busy"/> (the other device may leave) are; identity, credential,
    /// version and protocol failures need the user and are not.
    /// </summary>
    public static bool ShouldRetry(ConnectFailure failure) => failure switch
    {
        ConnectFailure.ConnectionFailed or ConnectFailure.ConnectionClosed or ConnectFailure.Timeout or ConnectFailure.Busy => true,
        _ => false,
    };

    /// <summary>
    /// Whether an established session that ended should be re-established automatically. A lost link, a timeout,
    /// a server restart or a plain close are; a local disconnect, a takeover by another device (reconnecting would
    /// fight over the session), being unpaired, and protocol/peer/internal errors are not.
    /// </summary>
    public static bool ShouldReconnect(SessionEndReason reason) => reason switch
    {
        SessionEndReason.ConnectionLost or SessionEndReason.Timeout or SessionEndReason.PeerClosed or SessionEndReason.Shutdown => true,
        _ => false,
    };

    /// <summary>
    /// The server to auto-connect to: the compatible discovered server advertising <paramref name="lastUsedFingerprint"/>,
    /// provided this phone still holds a device key for it. Null when there is none (empty fingerprint = no last-used server).
    /// </summary>
    public static DiscoveredServer? SelectAutoConnectTarget(
        IReadOnlyList<DiscoveredServer> discovered,
        ReadOnlySpan<byte> lastUsedFingerprint,
        ClientCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return SelectAutoConnectTarget(discovered, lastUsedFingerprint, server => credentials.IsPaired(server.Fingerprint.Span));
    }

    /// <summary>
    /// As <see cref="SelectAutoConnectTarget(IReadOnlyList{DiscoveredServer}, ReadOnlySpan{byte}, ClientCredentialStore)"/>,
    /// with a caller-supplied (e.g. cached) paired check, called at most once, for the matching server only.
    /// </summary>
    public static DiscoveredServer? SelectAutoConnectTarget(
        IReadOnlyList<DiscoveredServer> discovered,
        ReadOnlySpan<byte> lastUsedFingerprint,
        Func<DiscoveredServer, bool> isPaired)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(isPaired);
        if (lastUsedFingerprint.Length != Security.CertificateFingerprint.Length)
        {
            return null;
        }

        for (int i = 0; i < discovered.Count; i++)
        {
            DiscoveredServer server = discovered[i];
            if (server.IsCompatible && server.HasFingerprint(lastUsedFingerprint))
            {
                return isPaired(server) ? server : null;
            }
        }

        return null;
    }
}
