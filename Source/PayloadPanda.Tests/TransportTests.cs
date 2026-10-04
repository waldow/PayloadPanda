using System.Net;
using System.Net.Sockets;
using System.Text;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

/// <summary>
/// Sends real requests to a loopback server and checks the bytes that arrive, so both
/// transports are verified on the wire rather than through mocks.
/// </summary>
public class TransportTests
{
    private const string CannedResponse =
        "HTTP/1.1 200 OK\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "Vary: Origin\r\n" +
        "Vary: Accept-Encoding\r\n" +
        "Set-Cookie: a=1; Path=/\r\n" +
        "Set-Cookie: b=2\r\n" +
        "Content-Length: 2\r\n" +
        "Connection: close\r\n\r\nok";

    private static RequestModel JsonApiPost(string baseUrl) => new()
    {
        Method = HttpMethodType.POST,
        Url = baseUrl + "/items",
        BodyMode = BodyMode.Json,
        BodyText = "{\"name\":\"José\"}",
        Headers =
        [
            new() { Key = "Content-Type", Value = "application/vnd.api+json" },
            new() { Key = "Connection", Value = "keep-alive" }
        ],
        QueryParams = [new() { Key = "tag", Value = "a" }, new() { Key = "tag", Value = "b" }],
        AuthMode = AuthMode.Bearer,
        AuthToken = "tok"
    };

    [Fact]
    public async Task HttpService_sends_the_composed_request_and_keeps_header_lines()
    {
        await using var server = new LoopbackServer();
        var received = server.RespondOnceAsync(CannedResponse);

        var response = await new HttpService().SendAsync(JsonApiPost(server.BaseUrl), CancellationToken.None);
        var request = await received;

        Assert.StartsWith("POST /items?tag=a&tag=b HTTP/1.1\r\n", request.Head);
        Assert.Contains("Content-Type: application/vnd.api+json\r\n", request.Head);
        Assert.DoesNotContain("application/json", request.Head);
        Assert.Contains("Authorization: Bearer tok\r\n", request.Head);
        Assert.Contains("User-Agent: PayloadPanda/1.0\r\n", request.Head);
        Assert.Equal("{\"name\":\"José\"}", request.Body);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("ok", response.Body);
        Assert.Equal("Origin, Accept-Encoding", response.GetHeader("Vary"));
        Assert.Equal(2, response.Headers.Count(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task HttpService_sends_a_content_type_row_even_without_a_body()
    {
        await using var server = new LoopbackServer();
        var received = server.RespondOnceAsync(CannedResponse);

        await new HttpService().SendAsync(new RequestModel
        {
            Url = server.BaseUrl + "/",
            Headers = [new() { Key = "Content-Type", Value = "application/json" }]
        }, CancellationToken.None);

        Assert.Contains("Content-Type: application/json\r\n", (await received).Head);
    }

    [Fact]
    public async Task RawSocketService_sends_the_same_request_with_one_connection_header()
    {
        await using var server = new LoopbackServer();
        var received = server.RespondOnceAsync(CannedResponse);

        var response = await new RawSocketService().SendAsync(JsonApiPost(server.BaseUrl), CancellationToken.None);
        var request = await received;

        Assert.StartsWith("POST /items?tag=a&tag=b HTTP/1.1\r\n", request.Head);
        Assert.Contains($"Host: 127.0.0.1:{server.Port}\r\n", request.Head);
        Assert.Contains("Content-Type: application/vnd.api+json\r\n", request.Head);
        Assert.Contains("Authorization: Bearer tok\r\n", request.Head);
        Assert.Contains($"Content-Length: {Encoding.UTF8.GetByteCount("{\"name\":\"José\"}")}\r\n", request.Head);
        Assert.Single(request.HeadLines, l => l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Connection: close\r\n", request.Head);
        Assert.Equal("{\"name\":\"José\"}", request.Body);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("ok", response.Body);
        Assert.Equal(2, response.Headers.Count(h => h.Key == "Set-Cookie"));
        Assert.NotNull(response.Diagnostics);
        Assert.InRange(response.Diagnostics!.Timings.TimeToFirstByteMs, 0, response.Diagnostics.Timings.TotalMs);
    }

    [Fact]
    public async Task RawSocketService_honours_a_host_row()
    {
        await using var server = new LoopbackServer();
        var received = server.RespondOnceAsync(CannedResponse);

        await new RawSocketService().SendAsync(new RequestModel
        {
            Url = server.BaseUrl + "/",
            Headers = [new() { Key = "Host", Value = "virtual.example" }]
        }, CancellationToken.None);

        var request = await received;
        Assert.Single(request.HeadLines, l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Host: virtual.example\r\n", request.Head);
    }

    // A file whose bytes include every value plus CRLF and "--" runs, which a broken
    // multipart writer or reader would trip over.
    private static byte[] TrickyFileBytes() =>
        [.. Enumerable.Range(0, 256).Select(i => (byte)i), .. "\r\n--\r\n----PayloadPandaBoundary\r\n"u8.ToArray()];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multipart_uploads_arrive_byte_for_byte(bool rawSocket)
    {
        using var files = new TempFiles();
        var path = files.Create("blob é.bin", TrickyFileBytes());
        await using var server = new LoopbackServer();
        var received = server.RespondOnceAsync(CannedResponse);

        var request = new RequestModel
        {
            Method = HttpMethodType.POST,
            Url = server.BaseUrl + "/upload",
            BodyMode = BodyMode.FormData,
            FormFields =
            [
                new() { Key = "name", Value = "Ada Lovelace" },
                new() { Key = "file", Kind = FormFieldKind.File, FilePath = path }
            ]
        };
        var response = rawSocket
            ? await new RawSocketService().SendAsync(request, CancellationToken.None)
            : await new HttpService().SendAsync(request, CancellationToken.None);
        var got = await received;

        var contentType = got.Header("Content-Type")!;
        Assert.StartsWith("multipart/form-data; boundary=----PayloadPandaBoundary", contentType);
        var boundary = contentType[(contentType.IndexOf("boundary=", StringComparison.Ordinal) + 9)..];
        var expected = RequestComposer.Compose(request, true, options: new ComposeOptions { Boundary = boundary }).Body.ToArray();
        Assert.Equal(expected, got.BodyBytes);
        Assert.Equal(expected.Length.ToString(), got.Header("Content-Length"));
        Assert.Equal(200, response.StatusCode);

        if (rawSocket)
        {
            Assert.Contains($"<file: blob é.bin, {TrickyFileBytes().Length} B, application/octet-stream>", response.Diagnostics!.RawRequest);
            Assert.DoesNotContain("----PayloadPandaBoundary\r\n\r\n--", response.Diagnostics.RawRequest);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Binary_uploads_stream_the_whole_file(bool rawSocket)
    {
        using var files = new TempFiles();
        var content = new byte[5 * 1024 * 1024];
        new Random(42).NextBytes(content);
        var path = files.Create("video.mp4", content);
        await using var server = new LoopbackServer();
        var received = server.RespondOnceAsync(CannedResponse);

        var request = new RequestModel
        {
            Method = HttpMethodType.PUT,
            Url = server.BaseUrl + "/bucket/video.mp4",
            BodyMode = BodyMode.Binary,
            BinaryFilePath = path
        };
        _ = rawSocket
            ? await new RawSocketService().SendAsync(request, CancellationToken.None)
            : await new HttpService().SendAsync(request, CancellationToken.None);
        var got = await received;

        Assert.Equal("video/mp4", got.Header("Content-Type"));
        Assert.Equal(content.Length, got.BodyBytes.Length);
        Assert.True(content.AsSpan().SequenceEqual(got.BodyBytes));
    }

    [Fact]
    public async Task Raw_mode_reports_a_missing_body_file_as_a_file_problem_not_a_dns_failure()
    {
        var request = new RequestModel
        {
            Method = HttpMethodType.POST,
            Url = "http://127.0.0.1:9/",
            BodyMode = BodyMode.Binary,
            BinaryFilePath = Path.Combine(Path.GetTempPath(), "pp-missing-" + Guid.NewGuid().ToString("N"))
        };

        await Assert.ThrowsAsync<BodyFileException>(() => new RawSocketService().SendAsync(request, CancellationToken.None));
    }

    private sealed record ReceivedRequest(string Head, byte[] BodyBytes)
    {
        public string Body => Encoding.UTF8.GetString(BodyBytes);
        public string[] HeadLines => Head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        public string? Header(string name) => HeadLines
            .FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public LoopbackServer() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        // Accepts one connection, captures the request (head + Content-Length body),
        // writes the canned response and closes.
        public async Task<ReceivedRequest> RespondOnceAsync(string response)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[4096];

            int headEnd;
            while ((headEnd = IndexOfHeadEnd(buffer)) < 0)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) throw new InvalidOperationException("Connection closed before the request head ended.");
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }

            var head = Encoding.UTF8.GetString(buffer.GetRange(0, headEnd + 4).ToArray());
            var lengthLine = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim());
            while (buffer.Count < headEnd + 4 + length)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) break;
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }

            var body = buffer.GetRange(headEnd + 4, Math.Min(length, buffer.Count - headEnd - 4)).ToArray();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            return new ReceivedRequest(head, body);
        }

        private static int IndexOfHeadEnd(List<byte> data)
        {
            for (var i = 0; i + 3 < data.Count; i++)
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            return -1;
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
