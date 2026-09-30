using System.Runtime.Versioning;
using System.Security.Cryptography;
using Bds.Core.Auth;

namespace Bds.Host.Platform;

/// <summary>DPAPI bound to the service account. Other accounts can't decrypt.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ISecretProtector
{
    static readonly byte[] Entropy = "BdsHeadless.v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
