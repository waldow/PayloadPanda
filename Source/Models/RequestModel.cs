using System.Text.Json.Serialization;

namespace PayloadPanda.Models;

public class RequestModel
{
    public HttpMethodType Method { get; set; } = HttpMethodType.GET;
    public string Url { get; set; } = string.Empty;
    public List<HeaderItemData> Headers { get; set; } = [];
    public List<QueryParamData> QueryParams { get; set; } = [];
    public BodyMode BodyMode { get; set; } = BodyMode.None;
    public string BodyText { get; set; } = string.Empty;
    public AuthMode AuthMode { get; set; } = AuthMode.None;
    public string AuthToken { get; set; } = string.Empty;
    public string AuthUsername { get; set; } = string.Empty;
    public string AuthPassword { get; set; } = string.Empty;
    public string ApiKeyHeader { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public bool FollowRedirects { get; set; } = true;

    // CORS troubleshooting
    public bool CorsEnabled { get; set; } = false;
    public string CorsOrigin { get; set; } = string.Empty;
    public string CorsRequestMethod { get; set; } = string.Empty;   // Access-Control-Request-Method (preflight)
    public string CorsRequestHeaders { get; set; } = string.Empty;  // Access-Control-Request-Headers (preflight)
    public bool CorsIncludeCredentials { get; set; } = false;

    // Explicit nulls in hand-edited or AI-generated JSON ({"headers": null})
    // overwrite the non-null defaults during deserialization; call after every
    // load from disk to restore the invariants the rest of the app relies on.
    public void Normalize()
    {
        Url ??= string.Empty;
        Headers ??= [];
        Headers.RemoveAll(h => h is null);
        QueryParams ??= [];
        QueryParams.RemoveAll(p => p is null);
        BodyText ??= string.Empty;
        AuthToken ??= string.Empty;
        AuthUsername ??= string.Empty;
        AuthPassword ??= string.Empty;
        ApiKeyHeader ??= string.Empty;
        ApiKeyValue ??= string.Empty;
        CorsOrigin ??= string.Empty;
        CorsRequestMethod ??= string.Empty;
        CorsRequestHeaders ??= string.Empty;

        foreach (var header in Headers)
        {
            header.Key ??= string.Empty;
            header.Value ??= string.Empty;
        }
        foreach (var param in QueryParams)
        {
            param.Key ??= string.Empty;
            param.Value ??= string.Empty;
        }
    }
}

public class HeaderItemData
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public class QueryParamData
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}
