using System.Text;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Net;

/// <summary>
/// A PenBridge server found by mDNS/DNS-SD (NSD on Android): its resolved address plus the parsed TXT record. Built
/// from untrusted network data by <see cref="TryCreate"/>; the fingerprint is only a hint until a connection pins
/// it (pairing binds the real certificate, reconnects require an exact match).
/// </summary>
public sealed class DiscoveredServer
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private DiscoveredServer(string serviceName, string name, string host, int port, byte[] fingerprint, ProtocolVersion version)
    {
        ServiceName = serviceName;
        Name = name;
        Host = host;
        Port = port;
        Fingerprint = fingerprint;
        Version = version;
    }

    /// <summary>The DNS-SD instance name (sanitized), unique per server on the link; the identity of a browse entry.</summary>
    public string ServiceName { get; }

    /// <summary>Display name: the TXT <c>name</c>, or the instance name when empty (sanitized peer text).</summary>
    public string Name { get; }

    /// <summary>Resolved host name or IP literal (IPv6 without brackets).</summary>
    public string Host { get; }

    public int Port { get; }

    /// <summary>The advertised SHA-256 certificate fingerprint (32 bytes).</summary>
    public ReadOnlyMemory<byte> Fingerprint { get; }

    public ProtocolVersion Version { get; }

    /// <summary>True when this build can talk to the server (same protocol major).</summary>
    public bool IsCompatible => Version.IsCompatibleWith(ProtocolInfo.Version);

    /// <summary>True when <paramref name="fingerprint"/> equals the advertised fingerprint.</summary>
    public bool HasFingerprint(ReadOnlySpan<byte> fingerprint) => Fingerprint.Span.SequenceEqual(fingerprint);

    /// <summary>
    /// Maps a resolved service to a model. TXT values are raw bytes (null for a key without value); the known keys
    /// must be valid UTF-8, unknown keys are ignored. False when the host, port or TXT record is invalid. Never throws
    /// for network-derived input.
    /// </summary>
    public static bool TryCreate(
        string? serviceName,
        string? host,
        int port,
        IEnumerable<KeyValuePair<string, byte[]?>>? txt,
        out DiscoveredServer? server)
    {
        server = null;
        if (string.IsNullOrEmpty(serviceName) || txt is null || port is < 1 or > ushort.MaxValue || !IsValidHost(host))
        {
            return false;
        }

        var entries = new List<KeyValuePair<string, string>>(3);
        foreach (KeyValuePair<string, byte[]?> entry in txt)
        {
            if (entry.Key is null)
            {
                continue;
            }

            bool known = IsKnownKey(entry.Key);
            if (!known)
            {
                continue;
            }

            if (entry.Value is null || !TryDecode(entry.Value, out string value))
            {
                return false;
            }

            entries.Add(new(entry.Key, value));
        }

        if (!BonjourTxtRecord.TryParse(entries, out BonjourTxtRecord? record))
        {
            return false;
        }

        string instance = PeerText.Sanitize(serviceName);
        string name = PeerText.Sanitize(record!.Name);
        server = new DiscoveredServer(instance, name.Length > 0 ? name : instance, host!, port, record.Fingerprint.ToArray(), record.Version);
        return true;
    }

    private static bool IsKnownKey(string key) =>
        string.Equals(key, BonjourTxtRecord.VersionKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, BonjourTxtRecord.FingerprintKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, BonjourTxtRecord.NameKey, StringComparison.OrdinalIgnoreCase);

    private static bool TryDecode(byte[] bytes, out string value)
    {
        value = string.Empty;
        if (bytes.Length > 255)
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsValidHost(string? host) =>
        !string.IsNullOrEmpty(host)
        && host.Length <= PairingQrPayload.MaxHostLength
        && Uri.CheckHostName(host) != UriHostNameType.Unknown;
}
