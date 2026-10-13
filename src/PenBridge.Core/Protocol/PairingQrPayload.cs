using System.Globalization;
using System.Text;

namespace PenBridge.Core.Protocol;

/// <summary>
/// The pairing QR code: <c>penbridge://pair?h=&lt;host&gt;&amp;p=&lt;port&gt;&amp;fp=&lt;b64url&gt;&amp;t=&lt;b64url token 32B&gt;&amp;v=1.0</c>.
/// Values are percent-encoded; unknown keys are ignored; duplicates are rejected. <see cref="ToString"/> redacts the token.
/// </summary>
public sealed class PairingQrPayload
{
    public const string Prefix = "penbridge://pair?";

    /// <summary>Longest accepted payload text; anything longer is rejected before parsing.</summary>
    public const int MaxLength = 1024;

    /// <summary>Longest accepted host (DNS name limit).</summary>
    public const int MaxHostLength = 253;

    public PairingQrPayload(string host, ushort port, ReadOnlyMemory<byte> fingerprint, ReadOnlyMemory<byte> token, ProtocolVersion version)
    {
        if (!IsValidHost(host))
        {
            throw new ArgumentException("Invalid host.", nameof(host));
        }

        ArgumentOutOfRangeException.ThrowIfZero(port);
        if (fingerprint.Length != Base64UrlBytes.Length32)
        {
            throw new ArgumentException("Fingerprint must be 32 bytes.", nameof(fingerprint));
        }

        if (token.Length != Base64UrlBytes.Length32)
        {
            throw new ArgumentException("Token must be 32 bytes.", nameof(token));
        }

        Host = host;
        Port = port;
        Fingerprint = fingerprint;
        Token = token;
        Version = version;
    }

    /// <summary>Host name or IP literal (IPv6 without brackets).</summary>
    public string Host { get; }

    public ushort Port { get; }

    /// <summary>SHA-256 of the Mac's TLS certificate.</summary>
    public ReadOnlyMemory<byte> Fingerprint { get; }

    /// <summary>One-time J-PAKE secret (32 bytes). Never log it.</summary>
    public ReadOnlyMemory<byte> Token { get; }

    public ProtocolVersion Version { get; }

    /// <summary>Formats the QR text.</summary>
    public string Format() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Prefix}h={Uri.EscapeDataString(Host)}&p={Port}&fp={Base64UrlBytes.Encode(Fingerprint.Span)}&t={Base64UrlBytes.Encode(Token.Span)}&v={Version}");

    /// <summary>Diagnostic text without the token.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"PairingQrPayload(h={Host}, p={Port}, v={Version}, t=<redacted>)");

    /// <summary>Parses scanned QR text; false for anything that isn't a complete, valid payload. Never throws.</summary>
    public static bool TryParse(string? text, out PairingQrPayload? payload)
    {
        payload = null;
        if (text is null || text.Length > MaxLength || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string? host = null;
        string? port = null;
        string? fp = null;
        string? token = null;
        string? version = null;
        ReadOnlySpan<char> query = text.AsSpan(Prefix.Length);
        foreach (Range range in query.Split('&'))
        {
            ReadOnlySpan<char> pair = query[range];
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            ReadOnlySpan<char> key = pair[..eq];
            if (!TryUnescape(pair[(eq + 1)..], out string value))
            {
                return false;
            }

            bool ok = key switch
            {
                "h" => Assign(ref host, value),
                "p" => Assign(ref port, value),
                "fp" => Assign(ref fp, value),
                "t" => Assign(ref token, value),
                "v" => Assign(ref version, value),
                _ => true,
            };
            if (!ok)
            {
                return false;
            }
        }

        if (host is null || port is null || fp is null || token is null || version is null
            || !IsValidHost(host)
            || !TryParsePort(port, out ushort parsedPort)
            || !Base64UrlBytes.TryDecode32(fp, out byte[] fingerprint)
            || !Base64UrlBytes.TryDecode32(token, out byte[] tokenBytes)
            || !ProtocolVersion.TryParse(version, out ProtocolVersion parsedVersion))
        {
            return false;
        }

        payload = new PairingQrPayload(host, parsedPort, fingerprint, tokenBytes, parsedVersion);
        return true;
    }

    private static bool Assign(ref string? slot, string value)
    {
        if (slot is not null)
        {
            return false;
        }

        slot = value;
        return true;
    }

    private static bool TryUnescape(ReadOnlySpan<char> raw, out string value)
    {
        value = string.Empty;
        if (raw.IndexOf('%') < 0)
        {
            value = raw.ToString();
            return true;
        }

        // Strict percent-decoding into UTF-8; malformed escapes or invalid UTF-8 are rejected.
        Span<byte> bytes = stackalloc byte[MaxLength];
        int count = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '%')
            {
                if (i + 2 >= raw.Length
                    || !byte.TryParse(raw.Slice(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b))
                {
                    return false;
                }

                bytes[count++] = b;
                i += 2;
            }
            else if (c < 0x80)
            {
                bytes[count++] = (byte)c;
            }
            else
            {
                return false;
            }
        }

        if (!System.Text.Unicode.Utf8.IsValid(bytes[..count]))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes[..count]);
        return true;
    }

    private static bool TryParsePort(string text, out ushort port)
    {
        port = 0;
        if (text.Length is 0 or > 5)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port != 0;
    }

    private static bool IsValidHost(string? host) =>
        !string.IsNullOrEmpty(host)
        && host.Length <= MaxHostLength
        && Uri.CheckHostName(host) != UriHostNameType.Unknown;
}
