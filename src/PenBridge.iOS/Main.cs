using UIKit;

namespace PenBridge.iOS;

internal static class Application
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
