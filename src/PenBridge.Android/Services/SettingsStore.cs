using System.Globalization;
using Android.Content;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Android.Services;

/// <summary>Pad orientation preference (ADR-0019): landscape-locked by default.</summary>
public enum OrientationMode
{
    LockLandscape = 0,
    LockPortrait = 1,
    Auto = 2,
}

/// <summary>
/// A Mac this phone has connected to: the last address it was reached at and its display name, keyed by the pinned
/// certificate fingerprint. Holding a device key for the fingerprint (<c>ClientCredentialStore.IsPaired</c>) is what
/// makes it paired; this is only display/address metadata.
/// </summary>
public sealed record KnownMac(byte[] Fingerprint, string Name, string Host, int Port, DateTimeOffset LastUsed)
{
    /// <summary>Base64url fingerprint, the stable id used in preferences and UI lists.</summary>
    public string Id => CertificateFingerprint.ToBase64Url(Fingerprint);
}

/// <summary>
/// Non-secret preferences in SharedPreferences (<c>penbridge.settings</c>): last-used Mac, known Macs, orientation
/// mode, synthetic finger pressure. Secrets (device id, device keys) live in <see cref="AndroidKeystoreSecretStore"/>.
/// </summary>
/// <remarks>Safe to call from any thread (SharedPreferences is thread-safe; writes use <c>Apply</c>, which never blocks).</remarks>
public sealed class SettingsStore
{
    /// <summary>Default synthetic pressure for the finger fallback (half of full scale, as in Core's InputFilter).</summary>
    public const ushort DefaultFingerPressure = 0x8000;

    private const string PrefsName = "penbridge.settings";
    private const string LastMacKey = "mac.last";
    private const string KnownMacsKey = "mac.known";
    private const string OrientationKey = "pad.orientation";
    private const string FingerPressureKey = "pad.finger-pressure";
    private const string MacPrefix = "mac.";

    private readonly ISharedPreferences _prefs;

    public SettingsStore(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
            ?? throw new InvalidOperationException("SharedPreferences unavailable.");
    }

    /// <summary>Fingerprint of the most recently used Mac, or null.</summary>
    public byte[]? LastMacFingerprint =>
        Base64UrlBytes.TryDecode32(_prefs.GetString(LastMacKey, null) ?? string.Empty, out byte[] fp) ? fp : null;

    /// <summary>The most recently used Mac with its metadata, or null.</summary>
    public KnownMac? LastMac => LastMacFingerprint is { } fp ? GetKnownMac(fp) : null;

    public OrientationMode Orientation
    {
        get => _prefs.GetInt(OrientationKey, (int)OrientationMode.LockLandscape) switch
        {
            1 => OrientationMode.LockPortrait,
            2 => OrientationMode.Auto,
            _ => OrientationMode.LockLandscape,
        };
        set => Edit().PutInt(OrientationKey, (int)value)!.Apply();
    }

    /// <summary>Synthetic pressure (1..65535) for <c>InputFilterOptions.FingerFallbackPressure</c>.</summary>
    public ushort FingerPressure
    {
        get => (ushort)Math.Clamp(_prefs.GetInt(FingerPressureKey, DefaultFingerPressure), 1, ushort.MaxValue);
        set => Edit().PutInt(FingerPressureKey, Math.Max(1, (int)value))!.Apply();
    }

    /// <summary>All Macs connected to before, most recent first.</summary>
    public IReadOnlyList<KnownMac> GetKnownMacs()
    {
        ICollection<string>? ids = _prefs.GetStringSet(KnownMacsKey, null);
        var list = new List<KnownMac>(ids?.Count ?? 0);
        if (ids is null)
        {
            return list;
        }

        foreach (string id in ids)
        {
            if (Base64UrlBytes.TryDecode32(id, out byte[] fp) && GetKnownMac(fp) is { } mac)
            {
                list.Add(mac);
            }
        }

        list.Sort(static (a, b) => b.LastUsed.CompareTo(a.LastUsed));
        return list;
    }

    /// <summary>Metadata for the Mac with this fingerprint, or null.</summary>
    public KnownMac? GetKnownMac(ReadOnlySpan<byte> fingerprint)
    {
        if (fingerprint.Length != CertificateFingerprint.Length)
        {
            return null;
        }

        string id = CertificateFingerprint.ToBase64Url(fingerprint);
        string? host = _prefs.GetString(MacPrefix + id + ".host", null);
        int port = _prefs.GetInt(MacPrefix + id + ".port", 0);
        if (string.IsNullOrEmpty(host) || port is < 1 or > ushort.MaxValue)
        {
            return null;
        }

        string name = _prefs.GetString(MacPrefix + id + ".name", null) ?? host;
        long lastUsed = _prefs.GetLong(MacPrefix + id + ".used", 0);
        return new KnownMac(fingerprint.ToArray(), name, host, port, DateTimeOffset.FromUnixTimeMilliseconds(lastUsed));
    }

    /// <summary>Records a successful session: updates the Mac's metadata and makes it the last-used Mac.</summary>
    public void RecordConnected(ReadOnlySpan<byte> fingerprint, string name, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrEmpty(host);
        string id = CertificateFingerprint.ToBase64Url(fingerprint);
        var ids = new HashSet<string>(_prefs.GetStringSet(KnownMacsKey, null) ?? [], StringComparer.Ordinal) { id };
        Edit()
            .PutString(MacPrefix + id + ".name", name)!
            .PutString(MacPrefix + id + ".host", host)!
            .PutInt(MacPrefix + id + ".port", port)!
            .PutLong(MacPrefix + id + ".used", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!
            .PutStringSet(KnownMacsKey, ids)!
            .PutString(LastMacKey, id)!
            .Apply();
    }

    /// <summary>Removes a Mac's metadata (and the last-used mark if it pointed at it). Device keys are removed separately.</summary>
    public void Forget(ReadOnlySpan<byte> fingerprint)
    {
        string id = CertificateFingerprint.ToBase64Url(fingerprint);
        var ids = new HashSet<string>(_prefs.GetStringSet(KnownMacsKey, null) ?? [], StringComparer.Ordinal);
        ids.Remove(id);
        ISharedPreferencesEditor editor = Edit()
            .Remove(MacPrefix + id + ".name")!
            .Remove(MacPrefix + id + ".host")!
            .Remove(MacPrefix + id + ".port")!
            .Remove(MacPrefix + id + ".used")!
            .PutStringSet(KnownMacsKey, ids)!;
        if (string.Equals(_prefs.GetString(LastMacKey, null), id, StringComparison.Ordinal))
        {
            editor.Remove(LastMacKey);
        }

        editor.Apply();
    }

    /// <summary>Clears only the last-used mark (e.g. "Change Mac"), so startup shows the Mac list.</summary>
    public void ClearLastMac() => Edit().Remove(LastMacKey)!.Apply();

    private ISharedPreferencesEditor Edit() =>
        _prefs.Edit() ?? throw new InvalidOperationException("SharedPreferences editor unavailable.");

    /// <summary>Formats a host and port for display (IPv6 literals in brackets).</summary>
    public static string FormatEndpoint(string host, int port) =>
        host.Contains(':', StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"[{host}]:{port}")
            : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
}
