using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class HttpService
{
    public bool SslVerification { get; set; } = true;

    public async Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = request.FollowRedirects
        };

        if (!SslVerification)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds)
        };

        var url = BuildUrl(request);
        var method = request.Method switch
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

        using var httpRequest = new HttpRequestMessage(method, url);

        // Headers
        foreach (var header in request.Headers.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)))
        {
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Auth
        switch (request.AuthMode)
        {
            case AuthMode.Bearer:
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.AuthToken);
                break;
            case AuthMode.Basic:
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{request.AuthUsername}:{request.AuthPassword}"));
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                break;
            case AuthMode.ApiKey:
                httpRequest.Headers.TryAddWithoutValidation(
                    string.IsNullOrWhiteSpace(request.ApiKeyHeader) ? "X-API-Key" : request.ApiKeyHeader,
                    request.ApiKeyValue);
                break;
        }

        // Body
        if (request.BodyMode != BodyMode.None && method != HttpMethod.Get && method != HttpMethod.Head)
        {
            switch (request.BodyMode)
            {
                case BodyMode.Json:
                    httpRequest.Content = new StringContent(request.BodyText, Encoding.UTF8, "application/json");
                    break;
                case BodyMode.Xml:
                    httpRequest.Content = new StringContent(request.BodyText, Encoding.UTF8, "application/xml");
                    break;
                case BodyMode.Raw:
                    httpRequest.Content = new StringContent(request.BodyText, Encoding.UTF8, "text/plain");
                    break;
                case BodyMode.FormUrlEncoded:
                    httpRequest.Content = new StringContent(request.BodyText, Encoding.UTF8, "application/x-www-form-urlencoded");
                    break;
            }
        }

        var sw = Stopwatch.StartNew();
        using var httpResponse = await client.SendAsync(httpRequest, ct);
        sw.Stop();

        var bodyBytes = await httpResponse.Content.ReadAsByteArrayAsync(ct);
        var bodyText = Encoding.UTF8.GetString(bodyBytes);

        var responseHeaders = new Dictionary<string, string>();
        foreach (var header in httpResponse.Headers)
        {
            responseHeaders[header.Key] = string.Join("; ", header.Value);
        }
        foreach (var header in httpResponse.Content.Headers)
        {
            responseHeaders[header.Key] = string.Join("; ", header.Value);
        }

        return new ResponseModel
        {
            StatusCode = (int)httpResponse.StatusCode,
            ReasonPhrase = httpResponse.ReasonPhrase ?? string.Empty,
            Headers = responseHeaders,
            Body = bodyText,
            BodyBytes = bodyBytes,
            Duration = sw.Elapsed,
            ContentType = httpResponse.Content.Headers.ContentType?.ToString() ?? string.Empty,
            ResponseSize = bodyBytes.Length
        };
    }

    private static string BuildUrl(RequestModel request)
    {
        var url = request.Url.Trim();
        var enabledParams = request.QueryParams.Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key)).ToList();

        if (enabledParams.Count == 0)
            return url;

        var uriBuilder = new UriBuilder(url);
        var query = HttpUtility.ParseQueryString(uriBuilder.Query);

        foreach (var param in enabledParams)
        {
            query[param.Key] = param.Value;
        }

        uriBuilder.Query = query.ToString();
        return uriBuilder.ToString();
    }
}
