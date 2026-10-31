using System.Security.Cryptography;
using System.Text;

namespace Commander4K.Core.Security;

/// <summary>
/// Secrets stored in configuration files (AI keys, connection passwords, key passphrases): encrypted with Windows
/// DPAPI for the current user and prefixed <c>dpapi:</c>, so the files are useless on another account or machine.
/// Text without the prefix is returned as is, so a value typed into the JSON by hand still works.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "Commander4K.ai"u8.ToArray();

    public static string Protect(string secret) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>The plain secret, or <c>null</c> when it was encrypted by another user or machine (or is damaged).</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
