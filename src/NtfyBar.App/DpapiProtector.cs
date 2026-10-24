using System.Security.Cryptography;
using NtfyBar.Core;

namespace NtfyBar.App;

/// <summary>DPAPI, CurrentUser scope: config.json secrets are readable only by this Windows user on this PC.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] cipher) => ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
}
