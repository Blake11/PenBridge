using AppKit;

namespace PenBridge.Mac;

internal static class MainClass
{
    // NSApplication.delegate is a weak reference; this keeps the delegate (and everything it owns) alive.
    private static AppDelegate? s_delegate;

    private static void Main(string[] args)
    {
        NSApplication.Init();
        NSApplication app = NSApplication.SharedApplication;
        s_delegate = new AppDelegate();
        app.Delegate = s_delegate;
        app.Run();
    }
}
