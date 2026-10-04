using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class AiImportService
{
    public const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";

    // One long-lived client; the timeout is applied per request, so reconfiguring never
    // has to dispose a client that may still have a request in flight.
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private string _endpoint = DefaultEndpoint;
    private string? _configuredApiKey;
    private TimeSpan _timeout = TimeSpan.FromSeconds(60);
    private const int MaxInputLength = 8000;

    public void Configure(string? apiKey, string? endpoint, int timeoutSeconds)
    {
        _configuredApiKey = apiKey;
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
        _timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 60);
    }

    public string[] AvailableModels { get; } = ["gpt-5-nano", "gpt-5-mini", "gpt-5", "gpt-5.2", "gpt-5.4-nano", "gpt-5.4-mini"];

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
          "bodyMode": "None|Raw|Json|Xml|FormUrlEncoded|FormData|Binary",
          "bodyText": "request body content (Raw, Json, Xml)",
          "formFields": [{"key": "field", "value": "text value", "type": "text|file", "filePath": "path if file", "contentType": "optional"}],
          "binaryFilePath": "path of the file sent as the whole body if Binary",
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
        - Detect body content type: JSON objects/arrays → bodyMode=Json; XML → bodyMode=Xml; key=value&key2=value2 → bodyMode=FormUrlEncoded with one formFields entry per pair (decoded, type "text"); other → bodyMode=Raw
        - For curl commands: -X → method, -H → headers, -d/--data → body, -u → Basic auth, -L → followRedirects=true, --max-time → timeoutSeconds
        - curl -F name=value or --form-string name=value → bodyMode=FormData with a "text" form field; -F name=@path (optionally ;type=T) → a "file" form field with filePath (and contentType); --data-urlencode name=value → bodyMode=FormUrlEncoded with a text field; --data-binary @path → bodyMode=Binary with binaryFilePath
        - Never put form or file data in bodyText; use formFields or binaryFilePath
        - Keep placeholder tokens as-is (<token>, {{variable}}, :param) and add a warning about each placeholder found
        - If the input is ambiguous or incomplete, make reasonable defaults and add warnings explaining assumptions
        - If you cannot determine a URL, use an empty string and add a warning
        """;

    public async Task<AiImportResult> ParseRequestAsync(string input, string model, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("AI endpoint is not a valid http(s) URL. Check Settings.");
        }

        // The OPENAI_API_KEY fallback is only ever sent to OpenAI itself, never to a
        // custom endpoint the variable wasn't meant for.
        var isOpenAi = endpointUri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
        var apiKey = !string.IsNullOrWhiteSpace(_configuredApiKey)
            ? _configuredApiKey
            : isOpenAi ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(isOpenAi
                ? "API key not found. Set it in Settings or the OPENAI_API_KEY environment variable."
                : "API key not found. Set it in Settings.");
        }

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
        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var responseJson = await SendAsync(request, cancellationToken);

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
        // InvalidOperationException: an element has the wrong JSON kind (e.g. "choices": null).
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
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

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (response.IsSuccessStatusCode)
                return body;

            var statusCode = (int)response.StatusCode;
            if (body.Length > 500)
                body = body[..500] + "…";

            var message = statusCode switch
            {
                401 => "Invalid API key (401). Check the key in Settings.",
                429 => "Rate limited by the AI provider (429). Wait a moment and retry.",
                _ => $"AI provider error ({statusCode}): {body}"
            };
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The AI request timed out after {_timeout.TotalSeconds:F0} seconds.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            // Connection-level failure (DNS, refused, TLS) — not an HTTP error response.
            throw new HttpRequestException($"Network error: {ex.Message}", ex);
        }
    }

    internal static RequestModel ConvertDtoToRequest(AiImportResponseDto dto)
    {
        var request = new RequestModel
        {
            Url = dto.Url ?? string.Empty,
            BodyText = dto.BodyText ?? string.Empty,
            AuthToken = dto.AuthToken ?? string.Empty,
            AuthUsername = dto.AuthUsername ?? string.Empty,
            AuthPassword = dto.AuthPassword ?? string.Empty,
            ApiKeyHeader = dto.ApiKeyHeader ?? RequestComposer.DefaultApiKeyHeader,
            ApiKeyValue = dto.ApiKeyValue ?? string.Empty,
            BinaryFilePath = dto.BinaryFilePath ?? string.Empty,
            TimeoutSeconds = dto.TimeoutSeconds ?? 30,
            FollowRedirects = dto.FollowRedirects ?? true
        };

        // Enum names only: TryParse alone would also accept numbers ("7") and flag lists.
        if (TryParseName<HttpMethodType>(dto.Method, out var method))
            request.Method = method;
        if (TryParseName<BodyMode>(dto.BodyMode, out var bodyMode))
            request.BodyMode = bodyMode;
        if (TryParseName<AuthMode>(dto.AuthMode, out var authMode))
            request.AuthMode = authMode;

        if (dto.FormFields is { Count: > 0 })
        {
            request.FormFields = dto.FormFields
                .Where(f => f is not null)
                .Select(f => new FormFieldData
                {
                    Key = f.Key ?? string.Empty,
                    Value = f.Value ?? string.Empty,
                    Kind = string.Equals(f.Type, "file", StringComparison.OrdinalIgnoreCase) ? FormFieldKind.File : FormFieldKind.Text,
                    FilePath = f.FilePath ?? string.Empty,
                    ContentType = f.ContentType ?? string.Empty
                })
                .ToList();
        }
        else if (request.BodyMode == BodyMode.FormUrlEncoded && request.BodyText.Length > 0)
        {
            // A model that put the pairs in bodyText anyway: read them leniently here, since
            // Normalize's strict (byte-exact) conversion would fall back to a raw body.
            request.FormFields = FormUrlEncoding.Parse(request.BodyText)
                .Select(p => new FormFieldData { Key = p.Key, Value = p.Value })
                .ToList();
        }

        // Convert headers
        if (dto.Headers is { Count: > 0 })
        {
            request.Headers = dto.Headers
                .Where(h => h is not null)
                .Select(h => new HeaderItemData { Key = h.Key ?? string.Empty, Value = h.Value ?? string.Empty, IsEnabled = true })
                .ToList();
        }

        // Convert query params
        if (dto.QueryParams is { Count: > 0 })
        {
            request.QueryParams = dto.QueryParams
                .Where(p => p is not null)
                .Select(p => new QueryParamData { Key = p.Key ?? string.Empty, Value = p.Value ?? string.Empty, IsEnabled = true })
                .ToList();
        }

        request.Normalize();
        return request;
    }

    private static bool TryParseName<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result) &&
        !string.IsNullOrWhiteSpace(value) && !char.IsDigit(value.Trim()[0]) && !value.Contains(',');
}
