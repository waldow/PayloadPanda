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
