using System.Security.Cryptography;
using Bds.Core.Auth;

namespace Bds.Host.Platform;

public static class HostPaths
{
    public const int Port = 5199;
    public const string ServiceName = "BdsHeadless";

    public static string DataDirectory()
    {
        if (Environment.GetEnvironmentVariable("BDS_DATA_DIR") is { Length: > 0 } custom) return custom;
        if (Environment.GetEnvironmentVariable("STATE_DIRECTORY") is { Length: > 0 } state) return state;
        var dir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BdsHeadless")
            : "/var/lib/bds-headless";
        return CanWrite(dir)
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BdsHeadless");
    }

    public static ISecretProtector CreateProtector(string dataDir)
    {
        if (OperatingSystem.IsWindows()) return new DpapiProtector();

        // systemd LoadCredentialEncrypted= (TPM2 or host key) when installed as a service.
        if (Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY") is { Length: > 0 } creds
            && File.Exists(Path.Combine(creds, "kek")))
            return AesGcmProtector.FromFile(Path.Combine(creds, "kek"));

        var keyFile = Path.Combine(dataDir, "secure", "kek");
        if (!File.Exists(keyFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
            File.SetUnixFileMode(Path.GetDirectoryName(keyFile)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes(32));
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return AesGcmProtector.FromFile(keyFile);
    }

    static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
