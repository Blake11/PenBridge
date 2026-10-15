using Foundation;
using ObjCRuntime;

namespace PenBridge.Mac.Input;

/// <summary>
/// Observes one distributed notification with <see cref="NSNotificationSuspensionBehavior.DeliverImmediately"/>.
/// AppKit suspends distributed notification delivery while the app is inactive, and a menu-bar (LSUIElement) app is
/// almost never active, so the block-based observer would see a change only on the next activation. Delivered on
/// the main thread. Create and dispose on the main thread.
/// </summary>
internal sealed class DistributedNotificationObserver : NSObject
{
    private readonly Action _handler;

    public DistributedNotificationObserver(string name, Action handler)
    {
        _handler = handler;
        NSDistributedNotificationCenter.DefaultCenter.AddObserver(
            this, new Selector("notified:"), name, null, NSNotificationSuspensionBehavior.DeliverImmediately);
    }

    [Export("notified:")]
    public void Notified(NSNotification notification)
    {
        _ = notification;
        _handler();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            NSDistributedNotificationCenter.DefaultCenter.RemoveObserver(this);
        }

        base.Dispose(disposing);
    }
}
