using System.Text.Json;

namespace Bds.Core.Auth;

public sealed class FileTokenStore(string path, ISecretProtector protector) : ITokenStore
{
    readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<TokenState?> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return null;
            var plain = protector.Unprotect(await File.ReadAllBytesAsync(path, ct));
            return JsonSerializer.Deserialize<TokenState>(plain);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(TokenState state, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(state)), ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
            {
                // Best effort overwrite before delete.
                var len = new FileInfo(path).Length;
                await File.WriteAllBytesAsync(path, new byte[len], ct);
                File.Delete(path);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
