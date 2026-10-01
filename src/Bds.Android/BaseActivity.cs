using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using Google.Android.Material.AppBar;

namespace Bds.Android;

/// <summary>Toolbar + edge-to-edge insets so content is never drawn under system bars.</summary>
public abstract class BaseActivity : AppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        WindowCompat.SetDecorFitsSystemWindows(Window!, false);
    }

    protected void SetScreen(int layoutId, bool showUp = true)
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var toolbar = new MaterialToolbar(this);
        root.AddView(toolbar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        var content = LayoutInflater.Inflate(layoutId, root, false)!;
        root.AddView(content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(root);

        SetSupportActionBar(toolbar);
        SupportActionBar?.SetDisplayHomeAsUpEnabled(showUp);
        ViewCompat.SetOnApplyWindowInsetsListener(root, new InsetsListener());
    }

    public override bool OnSupportNavigateUp()
    {
        Finish();
        return true;
    }

    protected void ShowError(Exception e)
    {
        AppLog.Add($"{GetType().Name}: {e}");
        Toast.MakeText(this, e.Message, ToastLength.Long)!.Show();
    }

    sealed class InsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout() | WindowInsetsCompat.Type.Ime())!;
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return WindowInsetsCompat.Consumed!;
        }
    }
}
