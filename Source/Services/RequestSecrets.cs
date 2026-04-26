using System.Text.Json;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

// Centralizes DPAPI handling for the three sensitive RequestModel fields:
// AuthToken, AuthPassword, ApiKeyValue. Used by anything that persists a
// RequestModel to %AppData% (history, saved-request library, autosave).
// File export/import bypass these helpers so shared JSON stays plaintext.
public static class RequestSecrets
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static RequestModel ProtectClone(RequestModel request)
    {
        var json = JsonSerializer.Serialize(request, s_options);
        var clone = JsonSerializer.Deserialize<RequestModel>(json, s_readOptions) ?? new RequestModel();
        clone.AuthToken = SecretProtector.Protect(clone.AuthToken) ?? string.Empty;
        clone.AuthPassword = SecretProtector.Protect(clone.AuthPassword) ?? string.Empty;
        clone.ApiKeyValue = SecretProtector.Protect(clone.ApiKeyValue) ?? string.Empty;
        return clone;
    }

    public static void UnprotectInPlace(RequestModel request)
    {
        request.AuthToken = SecretProtector.Unprotect(request.AuthToken) ?? string.Empty;
        request.AuthPassword = SecretProtector.Unprotect(request.AuthPassword) ?? string.Empty;
        request.ApiKeyValue = SecretProtector.Unprotect(request.ApiKeyValue) ?? string.Empty;
    }
}
