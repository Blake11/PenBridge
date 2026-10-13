using Foundation;
using UIKit;

namespace PenBridge.iOS;

[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
        {
            return;
        }

        var label = new UILabel
        {
            Text = "PenBridge (iOS placeholder)",
            TextAlignment = UITextAlignment.Center,
            TextColor = UIColor.Label,
        };

        var controller = new UIViewController();
        controller.View!.BackgroundColor = UIColor.SystemBackground;
        label.Frame = controller.View.Bounds;
        label.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
        controller.View.AddSubview(label);

        Window = new UIWindow(windowScene) { RootViewController = controller };
        Window.MakeKeyAndVisible();
    }
}
