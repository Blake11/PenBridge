using Foundation;

namespace PenBridge.Mac.Services;

/// <summary>
/// Non-secret user settings in <see cref="NSUserDefaults.StandardUserDefaults"/> (domain = bundle id). Secrets never
/// go here; they live in <see cref="KeychainSecretStore"/>.
/// </summary>
public sealed class SettingsStore
{
    private const string TargetDisplayUuidKey = "TargetDisplayUuid";
    private const string AllowTakeoverKey = "AllowTakeover";
    private const string PausedKey = "Paused";
    private const string RunAtStartupKey = "RunAtStartup";

    private readonly NSUserDefaults _defaults;

    public SettingsStore()
        : this(NSUserDefaults.StandardUserDefaults)
    {
    }

    public SettingsStore(NSUserDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _defaults = defaults;
    }

    /// <summary>The chosen target display (<c>CGDisplayCreateUUIDFromDisplayID</c> string), or null for the main display.</summary>
    public string? TargetDisplayUuid
    {
        get => _defaults.StringForKey(TargetDisplayUuidKey);
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                _defaults.RemoveObject(TargetDisplayUuidKey);
            }
            else
            {
                _defaults.SetString(value, TargetDisplayUuidKey);
            }
        }
    }

    /// <summary>"Allow takeover" (ADR-0010). Default false.</summary>
    public bool AllowTakeover
    {
        get => _defaults.BoolForKey(AllowTakeoverKey);
        set => _defaults.SetBool(value, AllowTakeoverKey);
    }

    /// <summary>Input paused. Default false.</summary>
    public bool Paused
    {
        get => _defaults.BoolForKey(PausedKey);
        set => _defaults.SetBool(value, PausedKey);
    }

    /// <summary>
    /// Mirror of the "Run at startup" choice. The truth is <see cref="LoginItem.Status"/>; this remembers what the user
    /// asked for, e.g. while approval in System Settings is pending.
    /// </summary>
    public bool RunAtStartup
    {
        get => _defaults.BoolForKey(RunAtStartupKey);
        set => _defaults.SetBool(value, RunAtStartupKey);
    }
}
