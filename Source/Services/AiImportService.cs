using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class AiImportService
{
    private HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private string _endpoint = "https://api.openai.com/v1/chat/completions";
    private string? _configuredApiKey;
    private const int MaxInputLength = 8000;

    public void Configure(string? apiKey, string? endpoint, int timeoutSeconds)
    {
        _configuredApiKey = apiKey;

        if (!string.IsNullOrWhiteSpace(endpoint))
            _endpoint = endpoint;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 60)
        };
    }

    public string[] AvailableModels { get; } = ["gpt-5-nano", "gpt-5-mini", "gpt-5", "gpt-5.2"];

    private static readonly JsonSerializerOptions s_requestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions s_responseParseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string SystemPrompt = """
        You are an API request parser. Given free-form text (curl commands, API documentation snippets, Swagger/OpenAPI fragments, or plain descriptions), extract a structured API request.

        Return ONLY valid JSON matching this exact schema:
        {
          "method": "GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS",
          "url": "https://...",
          "headers": [{"key": "Header-Name", "value": "header-value"}],
          "queryParams": [{"key": "param", "value": "value"}],
          "bodyMode": "None|Raw|Json|Xml|FormUrlEncoded",
          "bodyText": "request body content",
          "authMode": "None|Bearer|Basic|ApiKey",
          "authToken": "token if Bearer",
          "authUsername": "username if Basic",
          "authPassword": "password if Basic",
          "apiKeyHeader": "header name if ApiKey",
          "apiKeyValue": "key value if ApiKey",
          "timeoutSeconds": 30,
          "followRedirects": true,
          "warnings": ["list of warnings about ambiguities"]
        }

        Rules:
        - Extract query parameters from the URL into queryParams array AND remove them from the url field
        - Detect auth from headers: "Authorization: Bearer <token>" → authMode=Bearer, authToken=<token>; "Authorization: Basic <base64>" → decode to username:password; custom API key headers → authMode=ApiKey
        - Do NOT duplicate auth info in both headers and auth fields — if you extract auth, remove that header
        - Detect body content type: JSON objects/arrays → bodyMode=Json; XML → bodyMode=Xml; key=value&key2=value2 → bodyMode=FormUrlEncoded; other → bodyMode=Raw
        - For curl commands: -X → method, -H → headers, -d/--data → body, -u → Basic auth, -L → followRedirects=true, --max-time → timeoutSeconds
        - Keep placeholder tokens as-is (<token>, {{variable}}, :param) and add a warning about each placeholder found
        - If the input is ambiguous or incomplete, make reasonable defaults and add warnings explaining assumptions
        - If you cannot determine a URL, use an empty string and add a warning
        """;

    public async Task<AiImportResult> ParseRequestAsync(string input, string model, CancellationToken cancellationToken)
    {
        var apiKey = !string.IsNullOrWhiteSpace(_configuredApiKey)
            ? _configuredApiKey
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API key not found. Set it in Settings or the OPENAI_API_KEY environment variable.");

        var warnings = new List<string>();

        if (input.Length > MaxInputLength)
        {
            input = input[..MaxInputLength];
            warnings.Add($"Input was truncated to {MaxInputLength} characters.");
        }

        var requestBody = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = input }
            },
            response_format = new { type = "json_object" }
        };

        var json = JsonSerializer.Serialize(requestBody, s_requestJsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"Network error: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            throw statusCode switch
            {
                401 => new HttpRequestException("Invalid API key. Check your OPENAI_API_KEY."),
                429 => new HttpRequestException("Rate limited by OpenAI. Wait a moment and retry."),
                _ => new HttpRequestException($"OpenAI API error ({statusCode}): {errorBody}")
            };
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        // Extract choices[0].message.content from the OpenAI response
        string contentJson;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            contentJson = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? throw new JsonException("Empty content");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException("AI returned unexpected format. Try rephrasing your input.");
        }

        // Parse the content JSON into the DTO
        AiImportResponseDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<AiImportResponseDto>(contentJson, s_responseParseOptions)
                  ?? throw new JsonException("Null result");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("AI returned unexpected format. Try rephrasing your input.");
        }

        // Convert DTO to RequestModel with safe enum parsing
        var result = new AiImportResult
        {
            Request = ConvertDtoToRequest(dto),
            Warnings = warnings
        };

        if (dto.Warnings is { Count: > 0 })
            result.Warnings.AddRange(dto.Warnings);

        return result;
    }

    private static RequestModel ConvertDtoToRequest(AiImportResponseDto dto)
    {
        var request = new RequestModel
        {
            Url = dto.Url ?? string.Empty,
            BodyText = dto.BodyText ?? string.Empty,
            AuthToken = dto.AuthToken ?? string.Empty,
            AuthUsername = dto.AuthUsername ?? string.Empty,
            AuthPassword = dto.AuthPassword ?? string.Empty,
            ApiKeyHeader = dto.ApiKeyHeader ?? "X-API-Key",
            ApiKeyValue = dto.ApiKeyValue ?? string.Empty,
            TimeoutSeconds = dto.TimeoutSeconds ?? 30,
            FollowRedirects = dto.FollowRedirects ?? true
        };

        // Parse method enum
        if (Enum.TryParse<HttpMethodType>(dto.Method, true, out var method))
            request.Method = method;

        // Parse body mode enum
        if (!string.IsNullOrEmpty(dto.BodyMode) && Enum.TryParse<BodyMode>(dto.BodyMode, true, out var bodyMode))
            request.BodyMode = bodyMode;

        // Parse auth mode enum
        if (!string.IsNullOrEmpty(dto.AuthMode) && Enum.TryParse<AuthMode>(dto.AuthMode, true, out var authMode))
            request.AuthMode = authMode;

        // Convert headers
        if (dto.Headers is { Count: > 0 })
        {
            request.Headers = dto.Headers
                .Select(h => new HeaderItemData { Key = h.Key, Value = h.Value, IsEnabled = true })
                .ToList();
        }

        // Convert query params
        if (dto.QueryParams is { Count: > 0 })
        {
            request.QueryParams = dto.QueryParams
                .Select(p => new QueryParamData { Key = p.Key, Value = p.Value, IsEnabled = true })
                .ToList();
        }

        return request;
    }
}
