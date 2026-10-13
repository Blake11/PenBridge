using Android.Views;

namespace PenBridge.Android;

[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var label = new TextView(this)
        {
            Text = "PenBridge",
            Gravity = GravityFlags.Center,
            TextSize = 24,
        };

        SetContentView(label);
    }
}
