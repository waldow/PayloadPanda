using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

    public bool SslVerification { get; set; } = true;

    public async Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct)
    {
        var diag = new ConnectionDiagnostics();
        var phase = RequestPhase.Dns;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300)));
        var token = timeoutCts.Token;

        ComposedRequest composed;
        try
        {
            composed = RequestComposer.Compose(request, includeClientDefaults: true);
        }
        catch (Exception ex)
        {
            diag.FailedPhase = RequestPhase.Dns;
            diag.ErrorMessage = ex.Message;
            throw new RawSocketException($"Invalid request: {ex.Message}", diag, ex);
        }

        var uri = composed.Uri;
        var useSsl = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        // IdnHost is the punycode form for internationalized names (what DNS, SNI and the
        // Host header need) and drops the brackets from IPv6 literals.
        var host = uri.IdnHost;
        diag.Scheme = uri.Scheme;
        diag.Host = host;
        diag.Port = uri.IsDefaultPort ? (useSsl ? 443 : 80) : uri.Port;

        var totalSw = Stopwatch.StartNew();
        SslStream? ssl = null;
        try
        {
            // ---- DNS ----
            phase = RequestPhase.Dns;
            var dnsSw = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
            dnsSw.Stop();
            diag.Timings.DnsMs = dnsSw.Elapsed.TotalMilliseconds;
            diag.ResolvedIpAddresses = addresses.Select(a => a.ToString()).ToList();
            if (addresses.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);

            // ---- TCP connect ----
            phase = RequestPhase.TcpConnect;
            using var client = new TcpClient();
            var tcpSw = Stopwatch.StartNew();
            await client.ConnectAsync(addresses, diag.Port, token).ConfigureAwait(false);
            tcpSw.Stop();
            diag.Timings.TcpConnectMs = tcpSw.Elapsed.TotalMilliseconds;
            diag.ChosenIpAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
            diag.LocalEndpoint = client.Client.LocalEndPoint?.ToString();
            diag.RemoteEndpoint = client.Client.RemoteEndPoint?.ToString();

            Stream stream = client.GetStream();

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
                    TargetHost = host
                }, token).ConfigureAwait(false);
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
            var (headBytes, display) = BuildRawRequest(composed, uri, diag.Port, useSsl);
            diag.RawRequest = display;
            await stream.WriteAsync(headBytes, token).ConfigureAwait(false);
            if (composed.Body.Length > 0)
                await stream.WriteAsync(composed.Body, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            var requestSentMs = totalSw.Elapsed.TotalMilliseconds;

            // ---- read the response ----
            phase = RequestPhase.ReadResponse;
            var (rawBytes, firstByteMs, truncated) = await ReadAllAsync(stream, totalSw, token).ConfigureAwait(false);
            // Time the server took to start answering once the request was out — a phase of
            // its own, comparable with the DNS/TCP/TLS bars rather than a running total.
            diag.Timings.TimeToFirstByteMs = Math.Max(0, firstByteMs - requestSentMs);

            totalSw.Stop();
            diag.Timings.TotalMs = totalSw.Elapsed.TotalMilliseconds;

            var response = ParseResponse(rawBytes, diag);
            response.Duration = totalSw.Elapsed;
            response.Diagnostics = diag;
            response.IsTruncated = truncated;
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
        finally
        {
            ssl?.Dispose();
        }
    }

    // ---- request building ----

    private static (byte[] head, string display) BuildRawRequest(
        ComposedRequest composed, Uri uri, int port, bool useSsl)
    {
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var headers = new List<(string Key, string Value)>();

        // A Host row in the Headers grid wins (virtual-host testing), as it does in HttpClient.
        var userHost = composed.GetHeader("Host");
        if (userHost is null)
        {
            var hostName = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost}]" : uri.IdnHost;
            var isDefaultPort = (useSsl && port == 443) || (!useSsl && port == 80);
            headers.Add(("Host", isDefaultPort ? hostName : $"{hostName}:{port}"));
        }

        foreach (var (key, value) in composed.Headers)
        {
            // The read loop relies on the connection closing, so ours is the only Connection header.
            if (key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                headers.Insert(0, (key, value));
            else
                headers.Add((key, value));
        }

        if (composed.HasBody)
            headers.Add(("Content-Length", composed.Body.Length.ToString(CultureInfo.InvariantCulture)));
        headers.Add(("Connection", "close"));

        var sb = new StringBuilder();
        sb.Append($"{composed.Method} {pathAndQuery} HTTP/1.1\r\n");
        foreach (var (key, value) in headers)
            sb.Append($"{key}: {value}\r\n");
        sb.Append("\r\n");

        var head = sb.ToString();
        return (Encoding.UTF8.GetBytes(head), head + composed.BodyText);
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

    private static async Task<(byte[] bytes, double firstByteMs, bool truncated)> ReadAllAsync(
        Stream stream, Stopwatch sw, CancellationToken token)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[ReadBufferSize];
        var firstByteMs = 0.0;
        var first = true;
        int read;

        // The cap covers head + body, so a response that hits it is reported as truncated.
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (first)
            {
                firstByteMs = sw.Elapsed.TotalMilliseconds;
                first = false;
            }
            ms.Write(buffer, 0, read);
            if (ms.Length > ResponseText.MaxBodyBytes)
                return (ms.ToArray(), firstByteMs, true);
        }

        return (ms.ToArray(), firstByteMs, false);
    }

    internal static ResponseModel ParseResponse(byte[] raw, ConnectionDiagnostics diag)
    {
        var response = new ResponseModel();
        var heads = new StringBuilder();
        var offset = 0;
        List<KeyValuePair<string, string>> headers;

        while (true)
        {
            var sep = IndexOfHeaderSeparator(raw, offset);
            var headEnd = sep >= 0 ? sep : raw.Length;
            var headText = Encoding.ASCII.GetString(raw, offset, headEnd - offset);
            heads.Append(headText);
            offset = sep >= 0 ? sep + 4 : raw.Length;

            headers = ParseHead(headText, response);

            // 1xx responses (100 Continue, 103 Early Hints) are interim: the real response
            // follows on the same connection. 101 Switching Protocols is final.
            var isInterim = response.StatusCode is >= 100 and < 200 && response.StatusCode != 101;
            if (!isInterim || sep < 0)
                break;
            heads.Append("\r\n\r\n");
        }

        diag.RawResponseHead = heads.ToString();
        var bodyBytes = raw[offset..];

        var transferEncoding = ResponseModel.FindHeader(headers, "Transfer-Encoding");
        if (transferEncoding?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
            bodyBytes = DecodeChunked(bodyBytes);

        response.Headers = headers;
        response.ContentType = ResponseModel.FindHeader(headers, "Content-Type") ?? string.Empty;
        response.BodyBytes = bodyBytes;
        response.Body = ResponseText.Decode(bodyBytes, response.ContentType);
        response.ResponseSize = bodyBytes.Length;
        return response;
    }

    private static List<KeyValuePair<string, string>> ParseHead(string headText, ResponseModel response)
    {
        var lines = headText.Split("\r\n");

        // e.g. "HTTP/1.1 200 OK"
        var parts = lines[0].Split(' ', 3);
        if (parts.Length >= 2 && int.TryParse(parts[1], out var code))
        {
            response.StatusCode = code;
            response.ReasonPhrase = parts.Length >= 3 ? parts[2] : string.Empty;
        }

        // Every header line is kept, so repeated fields (Set-Cookie, Vary) all survive.
        var headers = new List<KeyValuePair<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            headers.Add(new(line[..idx].Trim(), line[(idx + 1)..].Trim()));
        }
        return headers;
    }

    private static int IndexOfHeaderSeparator(byte[] data, int start)
    {
        for (var i = start; i + 3 < data.Length; i++)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        return -1;
    }

    internal static byte[] DecodeChunked(byte[] body)
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
