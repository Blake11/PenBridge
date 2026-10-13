using AppKit;
using Foundation;

namespace PenBridge.Mac;

[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private NSStatusItem? _statusItem;

    public override void DidFinishLaunching(NSNotification notification)
    {
        NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Accessory;

        _statusItem = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Variable);

        var button = _statusItem.Button;
        var image = NSImage.GetSystemSymbol("pencil.tip", "PenBridge");
        if (image is not null)
        {
            image.Template = true;
            button.Image = image;
        }
        else
        {
            button.Title = "PB";
        }

        button.ToolTip = "PenBridge";
        _statusItem.Menu = BuildMenu();
    }

    private static NSMenu BuildMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };

        menu.AddItem(new NSMenuItem("PenBridge — not connected") { Enabled = false });
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem("Quit PenBridge", "q", (_, _) => NSApplication.SharedApplication.Terminate(null)));

        return menu;
    }
}
