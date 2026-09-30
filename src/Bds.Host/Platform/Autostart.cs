using System.Diagnostics;
using System.ServiceProcess;

namespace Bds.Host.Platform;

/// <summary>Start at boot. Windows: service start type. Linux: marker read by the boot unit's ExecCondition.</summary>
public sealed class Autostart(string dataDir, ILogger<Autostart> log)
{
    string MarkerFile => Path.Combine(dataDir, "autostart");

    public bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var sc = new ServiceController(HostPaths.ServiceName);
                return sc.StartType == ServiceStartMode.Automatic;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        return File.Exists(MarkerFile);
    }

    public async Task<string?> SetAsync(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("sc.exe", ["config", HostPaths.ServiceName, "start=", enabled ? "auto" : "demand"])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode == 0) return null;
            log.LogWarning("sc config failed: {Output}", output);
            return "Could not change the service start type. Reinstall to repair permissions.";
        }

        if (enabled) await File.WriteAllTextAsync(MarkerFile, "");
        else File.Delete(MarkerFile);
        return null;
    }
}
