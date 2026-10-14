using Android.Views;
using Android.Widget;

namespace PenBridge.Android.Connect;

/// <summary>
/// The activity's back stack of <see cref="Screen"/>s inside one container. Only the top screen's view is attached.
/// Non-full-screen pages are padded by the system-bar and keyboard insets (the app is edge-to-edge on API 35+, and
/// the pad leaves the window edge-to-edge on older releases). UI thread only.
/// </summary>
internal sealed class ScreenStack : Java.Lang.Object, View.IOnApplyWindowInsetsListener
{
    private readonly FrameLayout _container;
    private readonly List<Screen> _stack = [];
    private bool _resumed;
    private int _insetLeft;
    private int _insetTop;
    private int _insetRight;
    private int _insetBottom;

    public ScreenStack(FrameLayout container)
    {
        _container = container;
        _container.SetOnApplyWindowInsetsListener(this);
    }

    /// <summary>Raised after the top screen changed.</summary>
    public event EventHandler? Changed;

    public Screen? Top => _stack.Count == 0 ? null : _stack[^1];

    public int Count => _stack.Count;

    public void Push(Screen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        Detach(Top);
        _stack.Add(screen);
        Attach(screen);
    }

    /// <summary>Pops the top screen (never the last one). False when only the root is left.</summary>
    public bool Pop()
    {
        if (_stack.Count <= 1)
        {
            return false;
        }

        Screen top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        Detach(top, dispose: true);
        Attach(Top!);
        return true;
    }

    /// <summary>Pops every screen above the root.</summary>
    public void PopToRoot()
    {
        if (_stack.Count <= 1)
        {
            return;
        }

        Detach(Top, dispose: true);
        _stack.RemoveAt(_stack.Count - 1);
        DisposeAboveRoot();
        Attach(Top!);
    }

    /// <summary>Replaces everything above the root with <paramref name="screen"/>.</summary>
    public void ReplaceAboveRoot(Screen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        bool topIsRoot = _stack.Count <= 1;
        Detach(Top, dispose: !topIsRoot);
        if (!topIsRoot)
        {
            _stack.RemoveAt(_stack.Count - 1);
        }

        DisposeAboveRoot();
        _stack.Add(screen);
        Attach(screen);
    }

    public void Resume()
    {
        _resumed = true;
        Top?.Resume();
    }

    public void Pause()
    {
        if (_resumed)
        {
            _resumed = false;
            Top?.Pause();
        }
    }

    public void Clear()
    {
        Pause();
        _container.RemoveAllViews();
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            _stack[i].Dispose();
        }

        _stack.Clear();
    }

    public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            global::Android.Graphics.Insets bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout() | WindowInsets.Type.Ime());
            (_insetLeft, _insetTop, _insetRight, _insetBottom) = (bars.Left, bars.Top, bars.Right, bars.Bottom);
        }
        else
        {
#pragma warning disable CS0618, CA1422 // API 29 has no typed insets; the system-window insets are its equivalent.
            (_insetLeft, _insetTop, _insetRight, _insetBottom) =
                (insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CS0618, CA1422
        }

        ApplyPadding();
        return insets;
    }

    private void Attach(Screen screen)
    {
        View view = screen.View;
        _container.AddView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        ApplyPadding();
        _container.RequestApplyInsets();
        if (_resumed)
        {
            screen.Resume();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pauses the screen, removes its view, and disposes it when it leaves the stack.</summary>
    private void Detach(Screen? screen, bool dispose = false)
    {
        if (screen is null)
        {
            return;
        }

        if (_resumed)
        {
            screen.Pause();
        }

        Ui.HideKeyboard(_container);
        _container.RemoveView(screen.View);
        if (dispose)
        {
            screen.Dispose();
        }
    }

    /// <summary>Disposes the (detached, never-shown-again) screens between the root and the top.</summary>
    private void DisposeAboveRoot()
    {
        for (int i = _stack.Count - 1; i >= 1; i--)
        {
            Screen screen = _stack[i];
            _stack.RemoveAt(i);
            screen.Dispose();
        }
    }

    private void ApplyPadding()
    {
        if (Top is { IsFullScreen: true })
        {
            _container.SetPadding(0, 0, 0, 0);
        }
        else
        {
            _container.SetPadding(_insetLeft, _insetTop, _insetRight, _insetBottom);
        }
    }
}
