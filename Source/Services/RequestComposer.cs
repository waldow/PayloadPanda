using System.IO;
using System.Security.Cryptography;
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

    /// <summary>The largest body the app will send (the sum of in-memory parts and files).</summary>
    public const long MaxUploadBytes = 1L << 30;

    /// <param name="includeClientDefaults">
    /// Add <c>User-Agent</c> and <c>Accept</c> when the user didn't set them. True for the
    /// app's own transports; false for cURL export, since curl adds its own.
    /// </param>
    /// <param name="methodOverride">
    /// Method used to decide whether a body is sent (the CORS preflight asks about a method
    /// other than the selected one). Defaults to the request's method.
    /// </param>
    /// <param name="options">
    /// <see cref="ComposeOptions.Describe"/> touches no files (cURL export, CORS header list);
    /// the default checks every body file exists and measures it, without reading it.
    /// </param>
    /// <exception cref="UriFormatException">The URL isn't absolute.</exception>
    /// <exception cref="FormatException">A header name is invalid or a value contains a line break.</exception>
    /// <exception cref="BodyFileException">A body file isn't chosen, doesn't exist, or the body is too large.</exception>
    public static ComposedRequest Compose(RequestModel request, bool includeClientDefaults,
        string? methodOverride = null, ComposeOptions? options = null)
    {
        options ??= ComposeOptions.Default;
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

        var body = hasBody ? BuildBody(request, options) : ComposedBody.None;
        if (body.ForcedContentType is { } forced)
        {
            // multipart/form-data carries the boundary in its Content-Type, so a Content-Type
            // row in the Headers grid would make the body unreadable: ours always wins.
            headers.RemoveAll(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
            headers.Add(new("Content-Type", forced));
        }
        else if (hasBody && !Contains(headers, "Content-Type"))
        {
            // Otherwise a Content-Type row in the Headers grid overrides the body-mode default.
            headers.Add(new("Content-Type", body.DefaultContentType));
        }

        if (includeClientDefaults)
        {
            if (!Contains(headers, "User-Agent"))
                headers.Add(new("User-Agent", DefaultUserAgent));
            if (!Contains(headers, "Accept"))
                headers.Add(new("Accept", "*/*"));
        }

        foreach (var (key, value) in headers)
            Validate(key, value);

        return new ComposedRequest(
            Method: request.Method,
            Uri: BuildUri(request),
            Headers: headers,
            HasBody: hasBody,
            BodyText: body.Text,
            Body: body.Wire,
            BodyPreview: body.Preview,
            FormParts: body.FormParts,
            BinaryFilePath: hasBody && request.BodyMode == BodyMode.Binary ? request.BinaryFilePath : null);
    }

    /// <summary>The enabled text fields of a URL-encoded body, in grid order (shared with the live preview).</summary>
    public static List<KeyValuePair<string, string>> UrlEncodedPairs(RequestModel request) =>
        request.FormFields
            .Where(f => f.IsEnabled && f.Kind == FormFieldKind.Text && !string.IsNullOrWhiteSpace(f.Key))
            .Select(f => new KeyValuePair<string, string>(f.Key.Trim(), f.Value ?? string.Empty))
            .ToList();

    private sealed record ComposedBody(
        string Text, WireBody Wire, string Preview, string DefaultContentType, string? ForcedContentType,
        IReadOnlyList<ComposedFormPart> FormParts)
    {
        public static ComposedBody None { get; } = new(string.Empty, WireBody.Empty, string.Empty, string.Empty, null, []);

        public static ComposedBody FromText(string text, string contentType) =>
            new(text, WireBody.FromBytes(Encoding.UTF8.GetBytes(text)), text, contentType, null, []);
    }

    private static ComposedBody BuildBody(RequestModel request, ComposeOptions options) => request.BodyMode switch
    {
        BodyMode.FormUrlEncoded => ComposedBody.FromText(
            FormUrlEncoding.Serialize(UrlEncodedPairs(request)), DefaultContentType(BodyMode.FormUrlEncoded)),
        BodyMode.FormData => BuildMultipart(request, options),
        BodyMode.Binary => BuildBinary(request, options),
        _ => ComposedBody.FromText(request.BodyText ?? string.Empty, DefaultContentType(request.BodyMode))
    };

    // multipart/form-data exactly as browsers write it (RFC 7578 + the WHATWG form-data
    // encoding): every part opens with --boundary, text parts carry no Content-Type unless
    // the row sets one, file parts send only the base file name, and the body closes with
    // --boundary--. MultipartFormDataContent isn't used: it adds a Content-Type to text
    // parts and RFC 2047-encodes non-ASCII names, which servers handle inconsistently.
    private static ComposedBody BuildMultipart(RequestModel request, ComposeOptions options)
    {
        var boundary = options.Boundary ?? NewBoundary();
        var segments = new List<BodySegment>();
        var pending = new MemoryStream();
        var preview = new StringBuilder();
        var parts = new List<ComposedFormPart>();

        void Write(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            pending.Write(bytes);
            preview.Append(text);
        }

        void FlushPending()
        {
            if (pending.Length == 0) return;
            segments.Add(new BytesSegment(pending.ToArray()));
            pending.SetLength(0);
        }

        foreach (var field in request.FormFields.Where(f => f.IsEnabled && !string.IsNullOrWhiteSpace(f.Key)))
        {
            var name = field.Key.Trim();
            var overrideType = string.IsNullOrWhiteSpace(field.ContentType) ? null : field.ContentType.Trim();
            if (overrideType is not null && (overrideType.Contains('\r') || overrideType.Contains('\n')))
                throw new FormatException($"The content type of form field \"{name}\" contains a line break.");

            Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{EscapeMultipartName(name)}\"");

            if (field.Kind == FormFieldKind.File)
            {
                var fileName = Path.GetFileName(field.FilePath ?? string.Empty);
                var contentType = overrideType ?? MimeTypes.FromFileName(fileName);
                parts.Add(new ComposedFormPart(name, FormFieldKind.File, string.Empty, field.FilePath ?? string.Empty, contentType));

                Write($"; filename=\"{EscapeMultipartName(fileName)}\"\r\nContent-Type: {contentType}\r\n\r\n");
                if (options.ResolveFiles)
                {
                    var length = MeasureFile(field.FilePath, $"form field \"{name}\"");
                    FlushPending();
                    segments.Add(new FileSegment(field.FilePath!, length));
                    preview.Append(FilePlaceholder(fileName, length, contentType));
                }
                else
                {
                    preview.Append(FilePlaceholder(fileName, null, contentType));
                }
                Write("\r\n");
            }
            else
            {
                var value = field.Value ?? string.Empty;
                parts.Add(new ComposedFormPart(name, FormFieldKind.Text, value, string.Empty, overrideType));
                Write(overrideType is null ? "\r\n\r\n" : $"\r\nContent-Type: {overrideType}\r\n\r\n");
                Write(value);
                Write("\r\n");
            }
        }

        Write($"--{boundary}--\r\n");
        FlushPending();

        var wire = options.ResolveFiles ? new WireBody(segments) : WireBody.Empty;
        EnsureWithinLimit(wire.Length, options);
        return new ComposedBody(string.Empty, wire, preview.ToString(), string.Empty,
            $"multipart/form-data; boundary={boundary}", parts);
    }

    private static ComposedBody BuildBinary(RequestModel request, ComposeOptions options)
    {
        var path = request.BinaryFilePath ?? string.Empty;
        var fileName = Path.GetFileName(path);
        var contentType = MimeTypes.FromFileName(fileName);
        if (!options.ResolveFiles)
            return new ComposedBody(string.Empty, WireBody.Empty, FilePlaceholder(fileName, null, contentType), contentType, null, []);

        var length = MeasureFile(path, "the body");
        EnsureWithinLimit(length, options);
        return new ComposedBody(string.Empty, new WireBody([new FileSegment(path, length)]),
            FilePlaceholder(fileName, length, contentType), contentType, null, []);
    }

    private static long MeasureFile(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new BodyFileException($"Choose a file for {what}.");

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new BodyFileException($"The file for {what} doesn't exist: {path}");
        return info.Length;
    }

    private static void EnsureWithinLimit(long length, ComposeOptions options)
    {
        if (length > options.MaxBodyBytes)
            throw new BodyFileException(
                $"The body is {ByteSize.Format(length)}; the upload limit is {ByteSize.Format(options.MaxBodyBytes)}.");
    }

    private static string FilePlaceholder(string fileName, long? length, string contentType) =>
        length is { } size
            ? $"<file: {fileName}, {ByteSize.Format(size)}, {contentType}>"
            : $"<file: {fileName}, {contentType}>";

    // Browsers escape only these three characters in part names and file names (WHATWG);
    // everything else, including non-ASCII, is sent as UTF-8.
    internal static string EscapeMultipartName(string value) =>
        value.Replace("\"", "%22").Replace("\r", "%0D").Replace("\n", "%0A");

    internal static string NewBoundary() =>
        "----PayloadPandaBoundary" +
        RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 16);

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
        BodyMode.FormData => "multipart/form-data",
        BodyMode.Binary => MimeTypes.Fallback,
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
/// <param name="BodyText">The body as text for text-like modes (for URL-encoded, the encoded string); empty for form-data and binary.</param>
/// <param name="Body">The bytes on the wire, streamed. Empty for form-data/binary when composed with <see cref="ComposeOptions.Describe"/>.</param>
/// <param name="BodyPreview">The body for display: text verbatim, files as <c>&lt;file: name, size, type&gt;</c> placeholders.</param>
/// <param name="FormParts">The multipart parts, in order (form-data only).</param>
/// <param name="BinaryFilePath">The file sent as the body (binary only).</param>
public sealed record ComposedRequest(
    HttpMethodType Method,
    Uri Uri,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    bool HasBody,
    string BodyText,
    WireBody Body,
    string BodyPreview,
    IReadOnlyList<ComposedFormPart> FormParts,
    string? BinaryFilePath)
{
    public string? GetHeader(string name) => ResponseModel.FindHeader(Headers, name);
}

/// <summary>One part of a multipart body. <see cref="ContentType"/> is the effective type (null for a plain text part).</summary>
public sealed record ComposedFormPart(string Name, FormFieldKind Kind, string Value, string FilePath, string? ContentType);

public sealed record ComposeOptions
{
    public static ComposeOptions Default { get; } = new();

    /// <summary>Describe the request without touching the file system (cURL export, CORS helpers).</summary>
    public static ComposeOptions Describe { get; } = new() { ResolveFiles = false };

    /// <summary>Check and measure body files. When false, file-backed bodies come back empty.</summary>
    public bool ResolveFiles { get; init; } = true;

    /// <summary>Fixed multipart boundary (tests); a random one by default.</summary>
    public string? Boundary { get; init; }

    public long MaxBodyBytes { get; init; } = RequestComposer.MaxUploadBytes;
}
