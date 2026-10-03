using System.Text;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <summary>
/// The one place that turns a <see cref="RequestModel"/> into what actually goes on the
/// wire: the final URI (Params grid applied), the ordered header list (user headers, auth,
/// Content-Type, client defaults) and the body. HttpService, RawSocketService, cURL export
/// and the CORS helpers all build on this, so they can't disagree about what a request means.
/// </summary>
public static class RequestComposer
{
    public const string DefaultUserAgent = "PayloadPanda/1.0";
    public const string DefaultApiKeyHeader = "X-API-Key";

    /// <param name="includeClientDefaults">
    /// Add <c>User-Agent</c> and <c>Accept</c> when the user didn't set them. True for the
    /// app's own transports; false for cURL export, since curl adds its own.
    /// </param>
    /// <param name="methodOverride">
    /// Method used to decide whether a body is sent (the CORS preflight asks about a method
    /// other than the selected one). Defaults to the request's method.
    /// </param>
    /// <exception cref="UriFormatException">The URL isn't absolute.</exception>
    /// <exception cref="FormatException">A header name is invalid or a value contains a line break.</exception>
    public static ComposedRequest Compose(RequestModel request, bool includeClientDefaults, string? methodOverride = null)
    {
        var method = string.IsNullOrWhiteSpace(methodOverride) ? request.Method.ToString() : methodOverride.Trim();
        var hasBody = request.BodyMode != BodyMode.None && MethodAllowsBody(method);
        var headers = new List<KeyValuePair<string, string>>();

        foreach (var header in request.Headers.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)))
        {
            var key = header.Key.Trim();
            // Both transports compute Content-Length from the actual body.
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            headers.Add(new(key, header.Value ?? string.Empty));
        }

        if (BuildAuthHeader(request) is { } auth)
        {
            // The Auth tab wins over a same-named row in the Headers grid.
            headers.RemoveAll(h => h.Key.Equals(auth.Key, StringComparison.OrdinalIgnoreCase));
            headers.Add(auth);
        }

        // A Content-Type row in the Headers grid overrides the body-mode default.
        if (hasBody && !Contains(headers, "Content-Type"))
            headers.Add(new("Content-Type", DefaultContentType(request.BodyMode)));

        if (includeClientDefaults)
        {
            if (!Contains(headers, "User-Agent"))
                headers.Add(new("User-Agent", DefaultUserAgent));
            if (!Contains(headers, "Accept"))
                headers.Add(new("Accept", "*/*"));
        }

        foreach (var (key, value) in headers)
            Validate(key, value);

        var bodyText = hasBody ? request.BodyText ?? string.Empty : string.Empty;
        return new ComposedRequest(
            Method: request.Method,
            Uri: BuildUri(request),
            Headers: headers,
            HasBody: hasBody,
            BodyText: bodyText,
            Body: Encoding.UTF8.GetBytes(bodyText));
    }

    /// <summary>
    /// The request URL with enabled Params-grid rows applied. Grid rows replace same-named
    /// parameters typed into the URL; repeated grid keys are all sent, in grid order. The
    /// rest of the URL is kept exactly as typed.
    /// </summary>
    public static Uri BuildUri(RequestModel request)
    {
        var url = request.Url.Trim();
        var gridParams = request.QueryParams
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key))
            .ToList();
        if (gridParams.Count == 0)
            return new Uri(url, UriKind.Absolute);

        var fragment = string.Empty;
        var hashIndex = url.IndexOf('#');
        if (hashIndex >= 0)
        {
            fragment = url[hashIndex..];
            url = url[..hashIndex];
        }

        var queryIndex = url.IndexOf('?');
        var path = queryIndex >= 0 ? url[..queryIndex] : url;
        var existingQuery = queryIndex >= 0 ? url[(queryIndex + 1)..] : string.Empty;

        var gridKeys = gridParams.Select(p => p.Key.Trim()).ToHashSet(StringComparer.Ordinal);
        var parts = existingQuery
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !gridKeys.Contains(DecodeQueryKey(segment)))
            .ToList();
        parts.AddRange(gridParams.Select(p =>
            $"{Uri.EscapeDataString(p.Key.Trim())}={Uri.EscapeDataString(p.Value ?? string.Empty)}"));

        return new Uri($"{path}?{string.Join('&', parts)}{fragment}", UriKind.Absolute);
    }

    public static bool MethodAllowsBody(string method) =>
        !method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
        !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    public static string DefaultContentType(BodyMode bodyMode) => bodyMode switch
    {
        BodyMode.Json => "application/json",
        BodyMode.Xml => "application/xml",
        BodyMode.FormUrlEncoded => "application/x-www-form-urlencoded",
        BodyMode.Raw => "text/plain",
        _ => string.Empty
    };

    private static KeyValuePair<string, string>? BuildAuthHeader(RequestModel request)
    {
        switch (request.AuthMode)
        {
            case AuthMode.Bearer:
                return new("Authorization", $"Bearer {request.AuthToken}".TrimEnd());
            case AuthMode.Basic:
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{request.AuthUsername}:{request.AuthPassword}"));
                return new("Authorization", $"Basic {credentials}");
            case AuthMode.ApiKey:
                var name = string.IsNullOrWhiteSpace(request.ApiKeyHeader) ? DefaultApiKeyHeader : request.ApiKeyHeader.Trim();
                return new(name, request.ApiKeyValue ?? string.Empty);
            default:
                return null;
        }
    }

    private static bool Contains(List<KeyValuePair<string, string>> headers, string name) =>
        headers.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string DecodeQueryKey(string segment)
    {
        var equalsIndex = segment.IndexOf('=');
        var key = equalsIndex >= 0 ? segment[..equalsIndex] : segment;
        return Uri.UnescapeDataString(key.Replace('+', ' '));
    }

    // Rejecting bad headers up front keeps the transports consistent: HttpClient would
    // silently drop an invalid name, while the raw socket would put it on the wire — and a
    // CR/LF in a value would let it inject extra header lines into the raw request.
    private static void Validate(string key, string value)
    {
        if (!key.All(IsTokenChar))
            throw new FormatException($"Header name \"{key}\" is not valid (letters, digits and !#$%&'*+-.^_`|~ only).");
        if (value.Contains('\r') || value.Contains('\n'))
            throw new FormatException($"Header \"{key}\" contains a line break.");
    }

    private static bool IsTokenChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ||
        "!#$%&'*+-.^_`|~".Contains(c);
}

/// <summary>A request as it will be sent. See <see cref="RequestComposer.Compose"/>.</summary>
public sealed record ComposedRequest(
    HttpMethodType Method,
    Uri Uri,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    bool HasBody,
    string BodyText,
    byte[] Body)
{
    public string? GetHeader(string name) => ResponseModel.FindHeader(Headers, name);
}
