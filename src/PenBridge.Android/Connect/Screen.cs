using Android.Views;
using PenBridge.Android.Services;

namespace PenBridge.Android.Connect;

/// <summary>
/// One page of the single-activity UI. The <see cref="ScreenStack"/> owns the lifecycle: <see cref="CreateView"/>
/// once, <see cref="Resume"/>/<see cref="Pause"/> while the screen is on top and the activity is resumed, and
/// <see cref="Dispose()"/> when it is popped. UI thread only.
/// </summary>
internal abstract class Screen : IDisposable
{
    private View? _view;

    protected Screen(MainActivity activity)
    {
        Activity = activity;
    }

    protected MainActivity Activity { get; }

    protected ClientHost Host => Activity.Host;

    /// <summary>True for screens that draw under the system bars themselves (the pad); others get inset padding.</summary>
    public virtual bool IsFullScreen => false;

    public View View => _view ??= CreateView();

    /// <summary>The host's state changed (forwarded by the activity while the screen is on top).</summary>
    public virtual void OnHostStateChanged(ClientHostState state)
    {
    }

    /// <summary>Discovery results changed (forwarded while the screen is on top).</summary>
    public virtual void OnDiscoveredChanged()
    {
    }

    /// <summary>System Back while on top. Return true when handled; false pops the screen.</summary>
    public virtual bool OnBack() => false;

    public virtual void Resume()
    {
    }

    public virtual void Pause()
    {
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected abstract View CreateView();

    protected virtual void Dispose(bool disposing)
    {
    }
}
