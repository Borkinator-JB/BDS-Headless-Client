using System.Diagnostics;
using System.Net.Http.Json;
using System.ServiceProcess;

namespace Bds.Tray;

static class ServiceControl
{
    const string Url = "http://127.0.0.1:5199";
    const string WindowsService = "BdsHeadless";
    const string LinuxUnit = "bds-headless.service";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Add("X-Bds-Local", "1");
        return http;
    }

    public static async Task<string> GetStatusAsync()
    {
        try
        {
            var s = await Http.GetFromJsonAsync<Status>("/api/status");
            if (s is null) return "BDS Headless";
            if (s.Account != "SignedIn") return "BDS Headless: not signed in";
            if (s.Server is null) return "BDS Headless: no server selected";
            return $"BDS Headless: {s.Server}, {s.Players} players, {s.Bot}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return "BDS Headless: service stopped";
        }
    }

    static async Task<bool> IsUpAsync()
    {
        try
        {
            using var res = await Http.GetAsync("/api/status");
            return res.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public static async Task<bool> EnsureRunningAsync()
    {
        if (await IsUpAsync()) return true;
        try
        {
            Start();
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return false;
        }
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            if (await IsUpAsync()) return true;
        }
        return false;
    }

    static void Start()
    {
        if (OperatingSystem.IsWindows() && TryStartWindowsService()) return;
        if (OperatingSystem.IsLinux() && File.Exists("/usr/lib/systemd/system/bds-headless.service"))
        {
            using var p = Process.Start("systemctl", ["start", LinuxUnit]);
            p.WaitForExit();
            return;
        }

        // Portable mode: no service installed, run the host next to this exe.
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "bds-headless-service.exe" : "bds-headless-service");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool TryStartWindowsService()
    {
        var service = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == WindowsService);
        if (service is null) return false;
        using (service)
            if (service.Status is ServiceControllerStatus.Stopped) service.Start();
        return true;
    }

    public static async Task StopAsync()
    {
        try
        {
            using var res = await Http.PostAsync("/api/shutdown", null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
        }
    }

    public static void OpenUi()
    {
        if (OperatingSystem.IsLinux()) Process.Start("xdg-open", Url)?.Dispose();
        else Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true })?.Dispose();
    }

    sealed record Status(string Account, string? Server, string Bot, int Players);
}
