using PenBridge.Android.Services;

namespace PenBridge.Android.Connect;

/// <summary>
/// A form screen that starts its own connect/pair attempts: it shows the host's error only when it came from an
/// attempt made here (not a stale error from an earlier screen), and whether an attempt is running.
/// </summary>
internal abstract class AttemptScreen : Screen
{
    private bool _attempted;

    protected AttemptScreen(MainActivity activity)
        : base(activity)
    {
    }

    protected bool IsBusy => Host.State.Status is ClientHostStatus.Connecting or ClientHostStatus.Pairing;

    public override void Resume() => Render(Host.State);

    public override void OnHostStateChanged(ClientHostState state) => Render(state);

    /// <summary>Call right before starting an attempt.</summary>
    protected void MarkAttempt() => _attempted = true;

    /// <summary>The error to show for <paramref name="state"/>, or null.</summary>
    protected string? ErrorFor(ClientHostState state) =>
        _attempted && state.Status == ClientHostStatus.Error ? ErrorText.Describe(state) : null;

    protected abstract void Render(ClientHostState state);
}
