using System.Globalization;
using PenBridge.Core.Security;
using PenBridge.Mac.Services;

namespace PenBridge.Mac.Pairing;

/// <summary>The pairing window's user-visible strings (no AppKit, so the rules read in one place).</summary>
internal static class PairingText
{
    public const string Waiting = "Waiting for your phone…";
    public const string Expired = "Expired, generating a new PIN…";
    public const string Confirming = "Confirming pairing…";
    public const string ServerDown = "The PenBridge server isn't running. Pairing resumes when it restarts.";

    /// <summary>"123456" → "123 456".</summary>
    public static string GroupPin(string pin) =>
        pin.Length == PinIssuer.PinLength ? string.Concat(pin.AsSpan(0, 3), " ", pin.AsSpan(3)) : pin;

    /// <summary>"123456" → "PIN 1 2 3 4 5 6", so VoiceOver reads digits, not "one hundred twenty-three".</summary>
    public static string SpokenPin(string pin)
    {
        var text = new System.Text.StringBuilder("PIN", 4 + (2 * pin.Length));
        foreach (char c in pin)
        {
            text.Append(' ').Append(c);
        }

        return text.ToString();
    }

    /// <summary>"Expires in 1:05".</summary>
    public static string Countdown(TimeSpan left)
    {
        int seconds = left <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(left.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"Expires in {seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>The status line for an open pairing window: lockout wait, failed attempts, or waiting.</summary>
    public static string Status(PairingInfo info, DateTimeOffset now)
    {
        if (now < info.LockedUntil)
        {
            int wait = (int)Math.Ceiling((info.LockedUntil - now).TotalSeconds);
            return string.Create(CultureInfo.InvariantCulture, $"Locked: try again in {wait} s");
        }

        if (info.AttemptsLeft < PinIssuer.MaxFailedAttempts)
        {
            return info.AttemptsLeft == 1
                ? "Wrong PIN, 1 attempt left"
                : string.Create(CultureInfo.InvariantCulture, $"Wrong PIN, {info.AttemptsLeft} attempts left");
        }

        return Waiting;
    }

    /// <summary>"Paired with Galaxy S23 Ultra" (<paramref name="deviceName"/> must already be sanitized).</summary>
    public static string Paired(string deviceName) => "Paired with " + deviceName;

    /// <summary>The manual-entry line(s) (ADR-0008): one "ip:port" per address.</summary>
    public static string Endpoints(IReadOnlyList<string> addresses, int port)
    {
        if (addresses.Count == 0)
        {
            return "No network connection";
        }

        var text = new System.Text.StringBuilder();
        foreach (string address in addresses)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(address).Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    public static string Instructions(string macName) =>
        "On your phone, open PenBridge and scan this code, or pick “" + macName + "” and enter the PIN.";
}
