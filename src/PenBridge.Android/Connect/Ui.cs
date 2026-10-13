using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace PenBridge.Android.Connect;

/// <summary>
/// Small view factory for the connect screens: Material 3 dark colour roles and shapes from <c>Resources/</c>, plain
/// platform widgets, layouts built in code (no inflation, nothing reflection-based).
/// </summary>
internal static class Ui
{
    /// <summary>Pages stay readable on tablets and in landscape: content is centred and at most this wide.</summary>
    private const float MaxContentWidthDp = 720;

    public static int Dp(Context context, float dp) =>
        (int)((dp * (context.Resources?.DisplayMetrics?.Density ?? 1f)) + 0.5f);

    public static Color ColorOf(Context context, int resourceId) => new(context.GetColor(resourceId));

    /// <summary>A vertical, scrolling page with standard padding; add content to <paramref name="content"/>.</summary>
    public static ScrollView Page(Context context, out LinearLayout content)
    {
        var scroll = new ScrollView(context) { FillViewport = true };
        content = new MaxWidthColumn(context, Dp(context, MaxContentWidthDp));
        int pad = Dp(context, 24);
        content.SetPadding(pad, Dp(context, 16), pad, pad);
        scroll.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.CenterHorizontal));
        return scroll;
    }

    public static LinearLayout Column(Context context) => new(context) { Orientation = global::Android.Widget.Orientation.Vertical };

    public static LinearLayout Row(Context context)
    {
        var row = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        return row;
    }

    /// <summary>A header row: optional back arrow, title, optional trailing action.</summary>
    public static LinearLayout Header(Context context, string title, Action? back, string? actionText = null, Action? action = null)
    {
        LinearLayout row = Row(context);
        row.SetPadding(0, 0, 0, Dp(context, 8));
        if (back is not null)
        {
            Button arrow = TextButton(context, "←", back);
            arrow.ContentDescription = "Back";
            arrow.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 26);
            arrow.SetPadding(0, 0, 0, Dp(context, 4));
            arrow.SetMinWidth(0);
            arrow.SetMinimumWidth(0);
            row.AddView(arrow, new LinearLayout.LayoutParams(Dp(context, 48), Dp(context, 48)));
        }

        TextView heading = Text(context, title, TextRole.Headline);
        row.AddView(heading, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        if (actionText is not null && action is not null)
        {
            row.AddView(TextButton(context, actionText, action), WrapLp());
        }

        return row;
    }

    public static TextView Text(Context context, string? text, TextRole role = TextRole.Body)
    {
        var view = new TextView(context) { Text = text };
        switch (role)
        {
            case TextRole.Headline:
                view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 26);
                view.SetTextColor(ColorOf(context, Resource.Color.pb_on_surface));
                break;
            case TextRole.Title:
                view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 17);
                view.SetTextColor(ColorOf(context, Resource.Color.pb_on_surface));
                view.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
                break;
            case TextRole.Label:
                view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
                view.SetTextColor(ColorOf(context, Resource.Color.pb_primary));
                view.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
                view.SetPadding(0, Dp(context, 20), 0, Dp(context, 8));
                break;
            case TextRole.Secondary:
                view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
                view.SetTextColor(ColorOf(context, Resource.Color.pb_on_surface_variant));
                break;
            default:
                view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
                view.SetTextColor(ColorOf(context, Resource.Color.pb_on_surface_variant));
                view.SetLineSpacing(Dp(context, 2), 1f);
                break;
        }

        return view;
    }

    public static Button FilledButton(Context context, string text, Action onClick) =>
        StyledButton(context, text, onClick, Resource.Drawable.pb_bg_filled, Resource.Color.pb_on_primary);

    public static Button TonalButton(Context context, string text, Action onClick) =>
        StyledButton(context, text, onClick, Resource.Drawable.pb_bg_tonal, Resource.Color.pb_on_primary_container);

    public static Button TextButton(Context context, string text, Action onClick) =>
        StyledButton(context, text, onClick, Resource.Drawable.pb_bg_text_button, Resource.Color.pb_primary);

    /// <summary>A tappable surface card (list row) with a title and a subtitle.</summary>
    public static LinearLayout Card(Context context, string title, string subtitle, Action? onClick, out TextView subtitleView)
    {
        LinearLayout card = Column(context);
        card.SetBackgroundResource(Resource.Drawable.pb_bg_card);
        int pad = Dp(context, 16);
        card.SetPadding(pad, Dp(context, 14), pad, Dp(context, 14));
        card.AddView(Text(context, title, TextRole.Title));
        subtitleView = Text(context, subtitle, TextRole.Secondary);
        card.AddView(subtitleView);
        if (onClick is not null)
        {
            card.Clickable = true;
            card.Focusable = true;
            card.Click += (_, _) => onClick();
        }
        else
        {
            card.Alpha = 0.6f;
        }

        return card;
    }

    /// <summary>An outlined single-line input.</summary>
    public static EditText Field(Context context, string hint, InputTypes inputType, int maxLength = 0)
    {
        var field = new EditText(context)
        {
            Hint = hint,
            InputType = inputType,
        };
        field.SetSingleLine(true);
        field.Typeface = Typeface.Default; // password variations otherwise switch the hint to monospace
        field.SetBackgroundResource(Resource.Drawable.pb_bg_field);
        field.SetTextColor(ColorOf(context, Resource.Color.pb_on_surface));
        field.SetHintTextColor(ColorOf(context, Resource.Color.pb_outline));
        field.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 17);
        int pad = Dp(context, 14);
        field.SetPadding(pad, pad, pad, pad);
        field.ImeOptions = ImeAction.Next;
        if (maxLength > 0)
        {
            field.SetFilters([new InputFilterLengthFilter(maxLength)]);
        }

        return field;
    }

    /// <summary>
    /// The 6-digit pairing PIN: digits only, Done action, no keyboard learning. Not masked: it is short-lived,
    /// single-use and already on the Mac's screen, and seeing it avoids typos that burn one of the 3 attempts.
    /// </summary>
    public static EditText PinField(Context context)
    {
        EditText field = Field(context, "6-digit PIN", InputTypes.ClassNumber, maxLength: 6);
        field.ImeOptions = ImeAction.Done | (ImeAction)ImeFlags.NoPersonalizedLearning;
        return field;
    }

    /// <summary>An error banner (hidden until <see cref="ShowBanner"/> gives it text).</summary>
    public static TextView ErrorBanner(Context context)
    {
        TextView banner = Text(context, null);
        banner.SetBackgroundResource(Resource.Drawable.pb_bg_error);
        banner.SetTextColor(ColorOf(context, Resource.Color.pb_on_error_container));
        int pad = Dp(context, 14);
        banner.SetPadding(pad, Dp(context, 12), pad, Dp(context, 12));
        banner.Visibility = ViewStates.Gone;
        return banner;
    }

    public static void ShowBanner(TextView banner, string? text)
    {
        banner.Text = text;
        banner.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    public static LinearLayout.LayoutParams FullWidthLp(Context context, float topMarginDp = 12)
    {
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lp.TopMargin = Dp(context, topMarginDp);
        return lp;
    }

    public static LinearLayout.LayoutParams WrapLp() =>
        new(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);

    /// <summary>Enables or disables a button with the Material disabled emphasis (38 %).</summary>
    public static void SetEnabled(View view, bool enabled)
    {
        view.Enabled = enabled;
        view.Alpha = enabled ? 1f : 0.38f;
    }

    public static void HideKeyboard(View view)
    {
        if (view.Context?.GetSystemService(Context.InputMethodService) is InputMethodManager imm)
        {
            imm.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
        }
    }

    private static Button StyledButton(Context context, string text, Action onClick, int background, int textColor)
    {
        var button = new Button(context) { Text = text };
        button.SetAllCaps(false);
        button.SetBackgroundResource(background);
        button.SetTextColor(ColorOf(context, textColor));
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        button.StateListAnimator = null;
        button.SetMinHeight(Dp(context, 48));
        int pad = Dp(context, 20);
        button.SetPadding(pad, 0, pad, 0);
        button.Click += (_, _) => onClick();
        return button;
    }
}

/// <summary>A vertical <see cref="LinearLayout"/> that never measures wider than its maximum width.</summary>
internal sealed class MaxWidthColumn : LinearLayout
{
    private readonly int _maxWidthPx;

    public MaxWidthColumn(Context context, int maxWidthPx)
        : base(context)
    {
        _maxWidthPx = maxWidthPx;
        Orientation = global::Android.Widget.Orientation.Vertical;
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        if (MeasureSpec.GetSize(widthMeasureSpec) > _maxWidthPx)
        {
            widthMeasureSpec = MeasureSpec.MakeMeasureSpec(_maxWidthPx, MeasureSpec.GetMode(widthMeasureSpec));
        }

        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
    }
}

internal enum TextRole
{
    Body,
    Secondary,
    Title,
    Headline,
    Label,
}
