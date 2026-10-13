using System.Globalization;
using PenBridge.Android.Services;

namespace PenBridge.Android.Connect;

/// <summary>
/// User-facing text for connection failures. Built only from the local <see cref="ClientErrorKind"/> (and the attempt
/// count): no text that came from the network, and not even the host's local message, is ever shown verbatim.
/// </summary>
internal static class ErrorText
{
    public static string Describe(ClientHostState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Error switch
        {
            ClientErrorKind.Unreachable => "Couldn't reach the Mac. Check that PenBridge is running on it and that both devices are on the same Wi-Fi.",
            ClientErrorKind.Timeout => "The Mac didn't answer in time.",
            ClientErrorKind.ConnectionLost => "The connection to the Mac was lost.",
            ClientErrorKind.IncompatibleVersion => "This app and PenBridge on the Mac aren't compatible. Update both to the latest version.",
            ClientErrorKind.IdentityMismatch => "The Mac's identity didn't match the one this phone knows, so PenBridge didn't connect. If you reinstalled PenBridge on the Mac, forget it in Settings and pair again.",
            ClientErrorKind.NotPaired => "This phone isn't paired with that Mac yet.",
            ClientErrorKind.PairingFailed => state.AttemptsLeft > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Wrong PIN. {state.AttemptsLeft} {(state.AttemptsLeft == 1 ? "attempt" : "attempts")} left.")
                : "That PIN or QR code didn't work.",
            ClientErrorKind.PairingLockedOut => "Too many wrong PINs. Wait for the Mac to show a new PIN, then try again.",
            ClientErrorKind.PairingExpired => "Pairing isn't open on the Mac, or the PIN expired. On the Mac, choose “Pair new device…” and use the code it shows.",
            ClientErrorKind.PairingUnavailable => "Another device is pairing with the Mac. Try again in a moment.",
            ClientErrorKind.AuthFailed => "The Mac no longer recognises this phone. Pair again.",
            ClientErrorKind.Busy => "Another device is using the Mac right now.",
            ClientErrorKind.TakenOver => "Another device took over the Mac.",
            ClientErrorKind.Unpaired => "The Mac removed this phone. Pair again to use it.",
            ClientErrorKind.InvalidInput => "Check the details you entered.",
            ClientErrorKind.Internal => "Something went wrong on this phone. Try again.",
            _ => "The connection failed.",
        };
    }
}
