using System.Text;

namespace PenBridge.Core.Protocol;

/// <summary>
/// The Bonjour/mDNS TXT record advertised by the Mac: <c>v=1.0</c>, <c>fp=&lt;base64url SHA-256 cert fingerprint&gt;</c>,
/// <c>name=&lt;Mac name&gt;</c>. Keys are case-insensitive (RFC 6763 §6.4); unknown keys are ignored.
/// </summary>
public sealed class BonjourTxtRecord
{
    public const string VersionKey = "v";
    public const string FingerprintKey = "fp";
    public const string NameKey = "name";

    /// <summary>Largest name in UTF-8 bytes so that <c>name=…</c> fits one 255-byte TXT string.</summary>
    public const int MaxNameBytes = 255 - 5;

    public BonjourTxtRecord(ProtocolVersion version, ReadOnlyMemory<byte> fingerprint, string name)
    {
        if (fingerprint.Length != Base64UrlBytes.Length32)
        {
            throw new ArgumentException("Fingerprint must be 32 bytes.", nameof(fingerprint));
        }

        ArgumentNullException.ThrowIfNull(name);
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
        {
            throw new ArgumentException($"Name exceeds {MaxNameBytes} UTF-8 bytes.", nameof(name));
        }

        Version = version;
        Fingerprint = fingerprint;
        Name = name;
    }

    public ProtocolVersion Version { get; }

    /// <summary>SHA-256 of the Mac's TLS certificate (32 bytes).</summary>
    public ReadOnlyMemory<byte> Fingerprint { get; }

    public string Name { get; }

    /// <summary>The TXT entries in advertisement order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToEntries() =>
    [
        new(VersionKey, Version.ToString()),
        new(FingerprintKey, Base64UrlBytes.Encode(Fingerprint.Span)),
        new(NameKey, Name),
    ];

    /// <summary>Parses entries discovered on the network; false when a field is missing, duplicated or invalid.</summary>
    public static bool TryParse(IEnumerable<KeyValuePair<string, string>> entries, out BonjourTxtRecord? record)
    {
        ArgumentNullException.ThrowIfNull(entries);
        record = null;
        string? v = null;
        string? fp = null;
        string? name = null;
        foreach (KeyValuePair<string, string> entry in entries)
        {
            if (!Take(entry, VersionKey, ref v) || !Take(entry, FingerprintKey, ref fp) || !Take(entry, NameKey, ref name))
            {
                return false;
            }
        }

        return TryParse(v, fp, name, out record);
    }

    /// <summary>Parses the three raw values; false when any is missing or invalid.</summary>
    public static bool TryParse(string? version, string? fingerprint, string? name, out BonjourTxtRecord? record)
    {
        record = null;
        if (version is null || fingerprint is null || name is null
            || !ProtocolVersion.TryParse(version, out ProtocolVersion parsedVersion)
            || !Base64UrlBytes.TryDecode32(fingerprint, out byte[] fp)
            || Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
        {
            return false;
        }

        record = new BonjourTxtRecord(parsedVersion, fp, name);
        return true;
    }

    private static bool Take(KeyValuePair<string, string> entry, string key, ref string? slot)
    {
        if (!string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (slot is not null || entry.Value is null)
        {
            return false;
        }

        slot = entry.Value;
        return true;
    }
}
