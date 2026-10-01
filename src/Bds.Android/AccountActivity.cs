using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Bds.Core.Auth;

namespace Bds.Android;

[Activity(Label = "@string/account")]
public sealed class AccountActivity : BaseActivity
{
    TextView _status = null!;
    TextView _code = null!;
    Button _open = null!;
    Button _action = null!;

    static XboxAccount Account => BdsApp.Bridge.Account;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.activity_account);

        _status = FindViewById<TextView>(Resource.Id.status)!;
        _code = FindViewById<TextView>(Resource.Id.code)!;
        _open = FindViewById<Button>(Resource.Id.open)!;
        _action = FindViewById<Button>(Resource.Id.action)!;

        _open.Click += (_, _) => CopyAndOpen();
        _action.Click += async (_, _) => await OnAction();
    }

    protected override async void OnResume()
    {
        base.OnResume();
        Account.Changed += OnChanged;
        try
        {
            await BdsApp.Bridge.InitializeAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            ShowError(e);
        }
        Refresh();
    }

    protected override void OnPause()
    {
        Account.Changed -= OnChanged;
        base.OnPause();
    }

    void OnChanged() => RunOnUiThread(Refresh);

    void Refresh()
    {
        var pending = Account.PendingCode;
        _code.Text = pending?.UserCode ?? "";
        _open.Visibility = pending is null ? ViewStates.Gone : ViewStates.Visible;
        (_status.Text, _action.Text) = Account.State switch
        {
            AccountState.SignedIn => ($"Signed in as {Account.Gamertag}", GetString(Resource.String.sign_out)),
            AccountState.WaitingForCode => ($"Open {pending?.VerificationUri} and enter this code. After approving, switch back to this app. It signs in by itself.", "Cancel"),
            AccountState.Error => (Account.Error ?? "Error", GetString(Resource.String.sign_in)),
            _ => ("Sign in with the Microsoft account the bot should use. A separate account is recommended.", GetString(Resource.String.sign_in)),
        };
    }

    async Task OnAction()
    {
        switch (Account.State)
        {
            case AccountState.SignedIn:
                await Account.SignOutAsync(CancellationToken.None);
                break;
            case AccountState.WaitingForCode:
                Account.CancelSignIn();
                break;
            default:
                try
                {
                    // Foreground service keeps the process alive while you are in the browser.
                    GatewayService.Start(this);
                    await Account.BeginSignInAsync(CancellationToken.None);
                    CopyAndOpen();
                }
                catch (Exception e)
                {
                    ShowError(e);
                    _status.Text = $"Could not start sign in: {e.Message}";
                }
                break;
        }
    }

    void CopyAndOpen()
    {
        if (Account.PendingCode is not { } pending) return;
        var clipboard = (ClipboardManager)GetSystemService(ClipboardService)!;
        clipboard.PrimaryClip = ClipData.NewPlainText("code", pending.UserCode);
        StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(pending.VerificationUri)));
    }
}
