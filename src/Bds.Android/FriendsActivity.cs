using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Bds.Core.Auth;
using Bds.Core.Storage;
using Bds.Core.Xbox;
using Google.Android.Material.MaterialSwitch;
using Microsoft.Extensions.DependencyInjection;

namespace Bds.Android;

[Activity(Label = "@string/friends")]
public sealed class FriendsActivity : BaseActivity
{
    ArrayAdapter<string> _friendsAdapter = null!;
    ArrayAdapter<string> _requestsAdapter = null!;
    List<XboxPerson> _friends = [];
    List<XboxPerson> _requests = [];
    TextView _error = null!;

    static SettingsStore Settings => BdsApp.Services.GetRequiredService<SettingsStore>();

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.activity_friends);

        _error = FindViewById<TextView>(Resource.Id.error)!;
        _friendsAdapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1);
        _requestsAdapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1);

        var list = FindViewById<ListView>(Resource.Id.list)!;
        list.Adapter = _friendsAdapter;
        list.ItemLongClick += (_, e) => ConfirmRemove(_friends[e.Position]);

        var requests = FindViewById<ListView>(Resource.Id.requests)!;
        requests.Adapter = _requestsAdapter;
        requests.ItemClick += async (_, e) => await Run(() => BdsApp.Bridge.Friends.AcceptAsync(_requests[e.Position], CancellationToken.None));

        FindViewById<Button>(Resource.Id.add)!.Click += async (_, _) =>
        {
            var input = FindViewById<EditText>(Resource.Id.gamertag)!;
            var tag = input.Text?.Trim() ?? "";
            if (tag.Length == 0) return;
            await Run(() => BdsApp.Bridge.Friends.AddByGamertagAsync(tag, CancellationToken.None));
            input.Text = "";
        };

        try
        {
            await BdsApp.Bridge.InitializeAsync(CancellationToken.None);
            var auto = FindViewById<MaterialSwitch>(Resource.Id.auto_accept)!;
            auto.Checked = await Settings.GetBoolAsync(SettingKeys.AutoAcceptFriends);
            auto.CheckedChange += async (_, e) => await Settings.SetBoolAsync(SettingKeys.AutoAcceptFriends, e.IsChecked);
            await Load();
        }
        catch (Exception e)
        {
            ShowError(e);
        }
    }

    async Task Load()
    {
        if (BdsApp.Bridge.Account.State != AccountState.SignedIn)
        {
            ShowError("Sign in first.");
            return;
        }
        try
        {
            _friends = (await BdsApp.Bridge.Friends.ListAsync(CancellationToken.None)).OrderBy(p => p.Gamertag).ToList();
            _requests = await BdsApp.Bridge.Friends.IncomingAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            AppLog.Add($"Friends: {e}");
            ShowError(e.Message);
            return;
        }
        _friendsAdapter.Clear();
        _friendsAdapter.AddAll(_friends.Select(p => $"{(p.Online ? "● " : "")}{p.Gamertag}").ToList());
        _requestsAdapter.Clear();
        _requestsAdapter.AddAll(_requests.Select(p => p.Gamertag).ToList());
        FindViewById<TextView>(Resource.Id.friends_title)!.Text =
            $"{GetString(Resource.String.friends)} ({_friends.Count}/{BdsApp.Bridge.Friends.Limit})";
    }

    void ConfirmRemove(XboxPerson p) =>
        new AndroidX.AppCompat.App.AlertDialog.Builder(this)
            .SetMessage($"Remove {p.Gamertag}?")!
            .SetPositiveButton("Remove", async (_, _) => await Run(() => BdsApp.Bridge.Friends.RemoveAsync(p, CancellationToken.None)))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();

    async Task Run(Func<Task> action)
    {
        _error.Visibility = ViewStates.Gone;
        try
        {
            await action();
        }
        catch (Exception e)
        {
            AppLog.Add($"Friends: {e}");
            ShowError(e.Message);
            return;
        }
        await Load();
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = ViewStates.Visible;
    }
}
