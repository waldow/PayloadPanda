using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <summary>
/// Sends an HTTP request over a hand-managed TCP (optionally TLS) socket instead of
/// <c>HttpClient</c>, so every layer of the connection is observable: DNS resolution,
/// the TCP connect, the TLS handshake and certificate chain, the exact request bytes,
/// and a per-phase timing breakdown. Mirrors <see cref="HttpService.SendAsync"/> so the
/// view-model can swap between the two transparently.
/// </summary>
public class RawSocketService
{
    private const int ReadBufferSize = 16 * 1024;
    private const long MaxResponseBytes = 64L * 1024 * 1024; // guard against runaway responses

    public bool SslVerification { get; set; } = true;

    public async Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct)
    {
        var diag = new ConnectionDiagnostics();
        var phase = RequestPhase.Dns;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300)));
        var token = timeoutCts.Token;

        Uri uri;
        try
        {
            uri = BuildUri(request);
        }
        catch (Exception ex)
        {
            diag.FailedPhase = RequestPhase.Dns;
            diag.ErrorMessage = ex.Message;
            throw new RawSocketException($"Invalid URL: {ex.Message}", diag, ex);
        }

        diag.Scheme = uri.Scheme;
        diag.Host = uri.Host;
        var useSsl = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        diag.Port = uri.IsDefaultPort ? (useSsl ? 443 : 80) : uri.Port;

        var totalSw = Stopwatch.StartNew();
        try
        {
            // ---- DNS ----
            phase = RequestPhase.Dns;
            var dnsSw = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, token);
            dnsSw.Stop();
            diag.Timings.DnsMs = dnsSw.Elapsed.TotalMilliseconds;
            diag.ResolvedIpAddresses = addresses.Select(a => a.ToString()).ToList();
            if (addresses.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);

            // ---- TCP connect ----
            phase = RequestPhase.TcpConnect;
            using var client = new TcpClient();
            var tcpSw = Stopwatch.StartNew();
            await client.ConnectAsync(addresses, diag.Port, token);
            tcpSw.Stop();
            diag.Timings.TcpConnectMs = tcpSw.Elapsed.TotalMilliseconds;
            diag.ChosenIpAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
            diag.LocalEndpoint = client.Client.LocalEndPoint?.ToString();
            diag.RemoteEndpoint = client.Client.RemoteEndPoint?.ToString();

            Stream stream = client.GetStream();
            SslStream? ssl = null;

            // ---- TLS handshake (https only) ----
            if (useSsl)
            {
                phase = RequestPhase.TlsHandshake;
                var policyErrors = SslPolicyErrors.None;
                ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, chain, errors) =>
                {
                    // Record validation result and certificate chain, but never reject here —
                    // raw mode must be able to display even invalid/expired certificates.
                    policyErrors = errors;
                    CaptureCertificates(cert, chain, diag);
                    return true;
                });

                var tlsSw = Stopwatch.StartNew();
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = uri.Host
                }, token);
                tlsSw.Stop();

                diag.Timings.TlsHandshakeMs = tlsSw.Elapsed.TotalMilliseconds;
                diag.TlsHandshakeSucceeded = true;
                diag.TlsProtocol = ssl.SslProtocol.ToString();
                diag.CipherSuite = ssl.NegotiatedCipherSuite.ToString();
                diag.SslPolicyErrors = policyErrors.ToString();
                stream = ssl;

                if (SslVerification && policyErrors != SslPolicyErrors.None)
                    throw new AuthenticationException($"certificate validation failed ({policyErrors})");
            }

            // ---- build + send the raw request ----
            phase = RequestPhase.SendRequest;
            var (headText, bodyBytes, display) = BuildRawRequest(request, uri, diag.Port, useSsl);
            diag.RawRequest = display;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headText), token);
            if (bodyBytes.Length > 0)
                await stream.WriteAsync(bodyBytes, token);
            await stream.FlushAsync(token);

            // ---- read the response ----
            phase = RequestPhase.ReadResponse;
            var (rawBytes, ttfbMs) = await ReadAllAsync(stream, totalSw, token);
            diag.Timings.TimeToFirstByteMs = ttfbMs;

            ssl?.Dispose();
            totalSw.Stop();
            diag.Timings.TotalMs = totalSw.Elapsed.TotalMilliseconds;

            var response = ParseResponse(rawBytes, diag);
            response.Duration = totalSw.Elapsed;
            response.Diagnostics = diag;
            return response;
        }
        catch (OperationCanceledException)
        {
            // Surfaced as-is so the view-model can distinguish user-cancel from timeout
            // via the outer token (the linked timeout token is internal to this method).
            throw;
        }
        catch (RawSocketException)
        {
            throw;
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            diag.Timings.TotalMs = totalSw.Elapsed.TotalMilliseconds;
            diag.FailedPhase = phase;
            diag.ErrorMessage = ex.Message;
            throw new RawSocketException(DescribePhaseFailure(phase, ex), diag, ex);
        }
    }

    // ---- URL / request building ----

    private static Uri BuildUri(RequestModel request)
    {
        var url = request.Url.Trim();
        var enabledParams = request.QueryParams.Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key)).ToList();
        if (enabledParams.Count == 0)
            return new Uri(url);

        var uriBuilder = new UriBuilder(url);
        var query = HttpUtility.ParseQueryString(uriBuilder.Query);
        foreach (var param in enabledParams)
            query[param.Key] = param.Value;
        uriBuilder.Query = query.ToString();
        return uriBuilder.Uri;
    }

    private static (string head, byte[] body, string display) BuildRawRequest(
        RequestModel request, Uri uri, int port, bool useSsl)
    {
        var method = request.Method.ToString();
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;

        // Body (UTF-8) — gated identically to HttpService.
        byte[] body = [];
        string? contentType = null;
        var hasBody = request.BodyMode != BodyMode.None
                      && request.Method != HttpMethodType.GET
                      && request.Method != HttpMethodType.HEAD
                      && !string.IsNullOrEmpty(request.BodyText);
        if (hasBody)
        {
            body = Encoding.UTF8.GetBytes(request.BodyText);
            contentType = request.BodyMode switch
            {
                BodyMode.Json => "application/json",
                BodyMode.Xml => "application/xml",
                BodyMode.FormUrlEncoded => "application/x-www-form-urlencoded",
                _ => "text/plain"
            };
        }

        var headers = new List<(string Key, string Value)>();

        var isDefaultPort = (useSsl && port == 443) || (!useSsl && port == 80);
        headers.Add(("Host", isDefaultPort ? uri.Host : $"{uri.Host}:{port}"));

        bool hasUserAgent = false, hasAccept = false, hasContentType = false;
        foreach (var h in request.Headers.Where(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key)))
        {
            headers.Add((h.Key, h.Value));
            if (h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) hasUserAgent = true;
            else if (h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) hasAccept = true;
            else if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) hasContentType = true;
        }

        switch (request.AuthMode)
        {
            case AuthMode.Bearer:
                headers.Add(("Authorization", $"Bearer {request.AuthToken}"));
                break;
            case AuthMode.Basic:
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{request.AuthUsername}:{request.AuthPassword}"));
                headers.Add(("Authorization", $"Basic {credentials}"));
                break;
            case AuthMode.ApiKey:
                headers.Add((string.IsNullOrWhiteSpace(request.ApiKeyHeader) ? "X-API-Key" : request.ApiKeyHeader,
                    request.ApiKeyValue));
                break;
        }

        if (!hasUserAgent) headers.Add(("User-Agent", "PayloadPanda/1.0"));
        if (!hasAccept) headers.Add(("Accept", "*/*"));
        if (hasBody)
        {
            if (!hasContentType && contentType != null) headers.Add(("Content-Type", contentType));
            headers.Add(("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture)));
        }
        // Force a non-persistent connection so the read loop terminates on EOF.
        headers.Add(("Connection", "close"));

        var sb = new StringBuilder();
        sb.Append($"{method} {pathAndQuery} HTTP/1.1\r\n");
        foreach (var (key, value) in headers)
            sb.Append($"{key}: {value}\r\n");
        sb.Append("\r\n");

        var head = sb.ToString();
        var display = hasBody ? head + request.BodyText : head;
        return (head, body, display);
    }

    // ---- TLS / certificate capture ----

    private static void CaptureCertificates(X509Certificate? cert, X509Chain? chain, ConnectionDiagnostics diag)
    {
        try
        {
            if (chain != null && chain.ChainElements.Count > 0)
            {
                for (var i = 0; i < chain.ChainElements.Count; i++)
                    diag.Certificates.Add(ToCertInfo(chain.ChainElements[i].Certificate, i));
            }
            else if (cert is X509Certificate2 leaf)
            {
                diag.Certificates.Add(ToCertInfo(leaf, 0));
            }
            else if (cert != null)
            {
                diag.Certificates.Add(ToCertInfo(X509CertificateLoader.LoadCertificate(cert.GetRawCertData()), 0));
            }
        }
        catch
        {
            // Certificate inspection is best-effort; never let it break the handshake.
        }
    }

    private static CertInfo ToCertInfo(X509Certificate2 cert, int index)
    {
        var sans = new List<string>();
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") // Subject Alternative Name
                continue;
            var formatted = ext.Format(multiLine: false);
            sans.AddRange(formatted
                .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return new CertInfo
        {
            Subject = cert.Subject,
            Issuer = cert.Issuer,
            NotBefore = cert.NotBefore,
            NotAfter = cert.NotAfter,
            Thumbprint = cert.Thumbprint,
            SerialNumber = cert.SerialNumber,
            SignatureAlgorithm = cert.SignatureAlgorithm?.FriendlyName ?? cert.SignatureAlgorithm?.Value ?? string.Empty,
            SubjectAlternativeNames = sans,
            ChainIndex = index
        };
    }

    // ---- response reading / parsing ----

    private static async Task<(byte[] bytes, double ttfbMs)> ReadAllAsync(
        Stream stream, Stopwatch sw, CancellationToken token)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[ReadBufferSize];
        var ttfbMs = 0.0;
        var first = true;
        int read;

        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (first)
            {
                ttfbMs = sw.Elapsed.TotalMilliseconds;
                first = false;
            }
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxResponseBytes)
                break;
        }

        return (ms.ToArray(), ttfbMs);
    }

    private static ResponseModel ParseResponse(byte[] raw, ConnectionDiagnostics diag)
    {
        var response = new ResponseModel();

        var sep = IndexOfHeaderSeparator(raw);
        var headBytes = sep >= 0 ? raw[..sep] : raw;
        var bodyBytes = sep >= 0 ? raw[(sep + 4)..] : [];

        var headText = Encoding.ASCII.GetString(headBytes);
        diag.RawResponseHead = headText;

        var lines = headText.Split("\r\n");
        if (lines.Length > 0)
        {
            // e.g. "HTTP/1.1 200 OK"
            var parts = lines[0].Split(' ', 3);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var code))
            {
                response.StatusCode = code;
                response.ReasonPhrase = parts.Length >= 3 ? parts[2] : string.Empty;
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        if (headers.TryGetValue("Transfer-Encoding", out var te) &&
            te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            bodyBytes = DecodeChunked(bodyBytes);
        }

        response.Headers = headers.ToDictionary(k => k.Key, v => v.Value);
        response.BodyBytes = bodyBytes;
        response.Body = Encoding.UTF8.GetString(bodyBytes);
        response.ContentType = headers.TryGetValue("Content-Type", out var ctv) ? ctv : string.Empty;
        response.ResponseSize = bodyBytes.Length;
        return response;
    }

    private static int IndexOfHeaderSeparator(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        return -1;
    }

    private static byte[] DecodeChunked(byte[] body)
    {
        using var output = new MemoryStream();
        var pos = 0;
        while (pos < body.Length)
        {
            var lineEnd = FindCrlf(body, pos);
            if (lineEnd < 0) break;

            var sizeToken = Encoding.ASCII.GetString(body, pos, lineEnd - pos).Trim();
            var semi = sizeToken.IndexOf(';'); // strip chunk extensions
            if (semi >= 0) sizeToken = sizeToken[..semi];

            if (!int.TryParse(sizeToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
                break;

            pos = lineEnd + 2;
            if (chunkSize <= 0) break;
            if (pos + chunkSize > body.Length) chunkSize = body.Length - pos;

            output.Write(body, pos, chunkSize);
            pos += chunkSize + 2; // skip the chunk data and its trailing CRLF
        }
        return output.ToArray();
    }

    private static int FindCrlf(byte[] data, int start)
    {
        for (var i = start; i + 1 < data.Length; i++)
            if (data[i] == 13 && data[i + 1] == 10)
                return i;
        return -1;
    }

    private static string DescribePhaseFailure(RequestPhase phase, Exception ex) => phase switch
    {
        RequestPhase.Dns => $"DNS resolution failed: {ex.Message}",
        RequestPhase.TcpConnect => $"TCP connection failed: {ex.Message}",
        RequestPhase.TlsHandshake => $"TLS handshake failed: {ex.Message}",
        RequestPhase.SendRequest => $"Sending request failed: {ex.Message}",
        RequestPhase.ReadResponse => $"Reading response failed: {ex.Message}",
        _ => ex.Message
    };
}
