using PayloadPanda.Models;

namespace PayloadPanda.Services;

// Centralizes DPAPI handling for the sensitive parts of a RequestModel: the auth
// fields (AuthToken, AuthPassword, ApiKeyValue) plus the values of well-known
// credential headers and query parameters. Used by anything that persists a
// RequestModel to %AppData% (history, saved-request library, tab session).
// File export/import bypass these helpers so shared JSON stays plaintext.
// Secrets anywhere else — a body, or a query string typed into the URL — stay plain.
public static class RequestSecrets
{
    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie"
    };

    private static readonly HashSet<string> SensitiveParamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "sig", "signature", "auth"
    };

    // Substrings that mark a header or query-parameter name as carrying a credential.
    private static readonly string[] SensitiveNameFragments =
        ["token", "secret", "password", "passwd", "apikey", "api-key", "api_key", "x-auth", "session"];

    public static RequestModel ProtectClone(RequestModel request)
    {
        var clone = request.Clone();
        clone.AuthToken = SecretProtector.Protect(clone.AuthToken) ?? string.Empty;
        clone.AuthPassword = SecretProtector.Protect(clone.AuthPassword) ?? string.Empty;
        clone.ApiKeyValue = SecretProtector.Protect(clone.ApiKeyValue) ?? string.Empty;

        foreach (var header in clone.Headers.Where(h => IsSensitiveHeaderName(h.Key)))
            header.Value = SecretProtector.Protect(header.Value) ?? string.Empty;
        foreach (var param in clone.QueryParams.Where(p => IsSensitiveParamName(p.Key)))
            param.Value = SecretProtector.Protect(param.Value) ?? string.Empty;

        return clone;
    }

    /// <returns>False when a secret couldn't be decrypted (it is left empty).</returns>
    public static bool UnprotectInPlace(RequestModel request)
    {
        var ok = true;
        request.AuthToken = Unprotect(request.AuthToken, ref ok);
        request.AuthPassword = Unprotect(request.AuthPassword, ref ok);
        request.ApiKeyValue = Unprotect(request.ApiKeyValue, ref ok);

        // Values without the DPAPI prefix pass through unchanged, so every row can be
        // checked — including rows whose name heuristics changed since they were saved.
        foreach (var header in request.Headers)
            header.Value = Unprotect(header.Value, ref ok);
        foreach (var param in request.QueryParams)
            param.Value = Unprotect(param.Value, ref ok);

        return ok;
    }

    internal static bool IsSensitiveHeaderName(string name) =>
        SensitiveHeaderNames.Contains(name.Trim()) || ContainsSensitiveFragment(name);

    internal static bool IsSensitiveParamName(string name) =>
        SensitiveParamNames.Contains(name.Trim()) || ContainsSensitiveFragment(name);

    private static bool ContainsSensitiveFragment(string name) =>
        SensitiveNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string Unprotect(string value, ref bool ok)
    {
        if (!SecretProtector.TryUnprotect(value, out var plaintext))
            ok = false;
        return plaintext ?? string.Empty;
    }
}
