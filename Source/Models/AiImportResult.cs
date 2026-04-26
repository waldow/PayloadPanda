using System.Text.Json.Serialization;

namespace PayloadPanda.Models;

public class AiImportResult
{
    public RequestModel Request { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>
/// Internal DTO for deserializing the AI response with string-typed enums for safe parsing.
/// </summary>
internal class AiImportResponseDto
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public List<AiHeaderDto>? Headers { get; set; }

    [JsonPropertyName("queryParams")]
    public List<AiQueryParamDto>? QueryParams { get; set; }

    [JsonPropertyName("bodyMode")]
    public string? BodyMode { get; set; }

    [JsonPropertyName("bodyText")]
    public string? BodyText { get; set; }

    [JsonPropertyName("authMode")]
    public string? AuthMode { get; set; }

    [JsonPropertyName("authToken")]
    public string? AuthToken { get; set; }

    [JsonPropertyName("authUsername")]
    public string? AuthUsername { get; set; }

    [JsonPropertyName("authPassword")]
    public string? AuthPassword { get; set; }

    [JsonPropertyName("apiKeyHeader")]
    public string? ApiKeyHeader { get; set; }

    [JsonPropertyName("apiKeyValue")]
    public string? ApiKeyValue { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("followRedirects")]
    public bool? FollowRedirects { get; set; }

    [JsonPropertyName("warnings")]
    public List<string>? Warnings { get; set; }
}

internal class AiHeaderDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

internal class AiQueryParamDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}
