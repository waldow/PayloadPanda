using System.Security.Cryptography;
using System.Text;

namespace PayloadPanda.Services;

// DPAPI wrapper used to encrypt at-rest secrets (API keys, auth tokens) before
// they hit %AppData%. CurrentUser scope: only the same Windows user on the same
// machine can decrypt. Plaintext that predates encryption is detected via the
// absence of the sentinel prefix and migrated on next save.
public static class SecretProtector
{
    private const string Prefix = "DPAPI:";

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext;

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;

        try
        {
            var encrypted = Convert.FromBase64String(value[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
