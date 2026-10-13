using AppKit;

namespace PenBridge.Mac;

internal static class MainClass
{
    private static void Main(string[] args)
    {
        NSApplication.Init();
        var app = NSApplication.SharedApplication;
        app.Delegate = new AppDelegate();
        app.Run();
    }
}
