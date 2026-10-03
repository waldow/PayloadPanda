using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class HttpService
{
    public bool SslVerification { get; set; } = true;

    // One client per (sslVerification, followRedirects) combination, reused across
    // requests so connections are pooled instead of exhausting sockets with a new
    // HttpClient per send. Timeouts are applied per request via a linked token.
    private readonly Dictionary<(bool Ssl, bool Redirects), HttpClient> _clients = [];
    private readonly object _clientsLock = new();

    private HttpClient GetClient(bool followRedirects)
    {
        var key = (Ssl: SslVerification, Redirects: followRedirects);
        lock (_clientsLock)
        {
            if (_clients.TryGetValue(key, out var existing))
                return existing;

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = key.Redirects,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            if (!key.Ssl)
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _clients[key] = client;
            return client;
        }
    }

    public async Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct)
    {
        var composed = RequestComposer.Compose(request, includeClientDefaults: true);
        var client = GetClient(request.FollowRedirects);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300)));
        var token = timeoutCts.Token;

        using var httpRequest = new HttpRequestMessage(ToHttpMethod(composed.Method), composed.Uri);

        HttpContent? content = composed.HasBody ? new ByteArrayContent(composed.Body) : null;
        foreach (var (key, value) in composed.Headers)
        {
            if (httpRequest.Headers.TryAddWithoutValidation(key, value))
                continue;

            // Content-Type and the other Content-* headers belong to HttpContent; the
            // request-header collection rejects them. A request without a body still
            // carries them, since some APIs insist on Content-Type even for a GET.
            content ??= new ByteArrayContent([]);
            content.Headers.TryAddWithoutValidation(key, value);
        }
        httpRequest.Content = content;

        var sw = Stopwatch.StartNew();
        using var httpResponse = await client
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        await using var stream = await httpResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var (bodyBytes, truncated) = await ResponseText
            .ReadCappedAsync(stream, ResponseText.MaxBodyBytes, token)
            .ConfigureAwait(false);
        sw.Stop();

        // NonValidated yields each header line as the server sent it, without the parsing
        // that would re-split or re-join values.
        var headers = new List<KeyValuePair<string, string>>();
        AddRawHeaders(headers, httpResponse.Headers.NonValidated);
        AddRawHeaders(headers, httpResponse.Content.Headers.NonValidated);
        var contentType = ResponseModel.FindHeader(headers, "Content-Type") ?? string.Empty;

        return new ResponseModel
        {
            StatusCode = (int)httpResponse.StatusCode,
            ReasonPhrase = httpResponse.ReasonPhrase ?? string.Empty,
            Headers = headers,
            Body = ResponseText.Decode(bodyBytes, contentType),
            BodyBytes = bodyBytes,
            Duration = sw.Elapsed,
            ContentType = contentType,
            ResponseSize = bodyBytes.Length,
            IsTruncated = truncated
        };
    }

    private static void AddRawHeaders(List<KeyValuePair<string, string>> target, HttpHeadersNonValidated source)
    {
        foreach (var (name, values) in source)
        {
            foreach (var value in values)
                target.Add(new(name, value));
        }
    }

    private static HttpMethod ToHttpMethod(HttpMethodType method) => method switch
    {
        HttpMethodType.GET => HttpMethod.Get,
        HttpMethodType.POST => HttpMethod.Post,
        HttpMethodType.PUT => HttpMethod.Put,
        HttpMethodType.DELETE => HttpMethod.Delete,
        HttpMethodType.PATCH => HttpMethod.Patch,
        HttpMethodType.HEAD => HttpMethod.Head,
        HttpMethodType.OPTIONS => HttpMethod.Options,
        _ => HttpMethod.Get
    };
}
