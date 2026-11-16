using System.Net;
using System.Net.Sockets;
using System.Text;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class ResponseFramerTests
{
    // Feeds the response one byte at a time (the worst case for separators that straddle
    // reads) and returns how many bytes had arrived when the framer called it complete.
    private static int? CompleteAt(string response, bool headRequest = false)
    {
        var bytes = Encoding.Latin1.GetBytes(response);
        var framer = new ResponseFramer(headRequest);
        for (var length = 1; length <= bytes.Length; length++)
        {
            if (framer.IsComplete(bytes, length))
                return length;
        }
        return null;
    }

    [Fact]
    public void Content_length_ends_the_response()
    {
        const string response = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello";

        Assert.Equal(response.Length, CompleteAt(response));
        Assert.Equal(response.Length, CompleteAt(response + "extra"));
    }

    [Fact]
    public void Final_chunk_and_trailers_end_a_chunked_response()
    {
        const string response =
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "5;ext=1\r\nhello\r\n" +
            "3\r\n0\r\n\r\n" +   // chunk data that looks like a final chunk
            "0\r\nX-Checksum: abc\r\n\r\n";

        Assert.Equal(response.Length, CompleteAt(response));
    }

    [Fact]
    public void Interim_responses_are_skipped_before_framing()
    {
        const string response =
            "HTTP/1.1 100 Continue\r\n\r\n" +
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok";

        Assert.Equal(response.Length, CompleteAt(response));
    }

    [Theory]
    [InlineData("HTTP/1.1 204 No Content\r\n\r\n", false)]
    [InlineData("HTTP/1.1 304 Not Modified\r\nContent-Length: 10\r\n\r\n", false)]
    [InlineData("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n", true)]
    public void Responses_without_a_body_end_at_the_head(string response, bool headRequest)
    {
        Assert.Equal(response.Length, CompleteAt(response, headRequest));
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\n\r\nbody until the server closes")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nnot-hex\r\n0\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhell")]
    public void Unframed_or_incomplete_responses_run_until_close(string response)
    {
        Assert.Null(CompleteAt(response));
    }

    [Fact]
    public void A_huge_declared_chunk_size_is_clamped_to_what_arrived()
    {
        var body = Encoding.ASCII.GetBytes("7FFFFFFF\r\nhello");

        Assert.Equal("hello", Encoding.ASCII.GetString(RawSocketService.DecodeChunked(body)));
    }

    [Fact]
    public async Task Raw_mode_returns_the_response_although_the_server_keeps_the_connection_open()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var release = new TaskCompletionSource();
        var server = ServeAsync(listener, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", release.Task);

        var response = await new RawSocketService().SendAsync(
            new RequestModel { Url = Url(listener), TimeoutSeconds = 10 }, CancellationToken.None);
        release.SetResult();
        await server;

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("ok", response.Body);
    }

    [Fact]
    public async Task Raw_mode_reports_a_connection_closed_without_a_response()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = ServeAsync(listener, string.Empty, Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<RawSocketException>(() => new RawSocketService().SendAsync(
            new RequestModel { Url = Url(listener), TimeoutSeconds = 10 }, CancellationToken.None));
        await server;

        Assert.Equal(RequestPhase.ReadResponse, ex.Diagnostics.FailedPhase);
        Assert.Contains("without sending a response", ex.Message);
    }

    [Fact]
    public async Task HttpService_keeps_no_cookie_jar_between_requests()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        const string response =
            "HTTP/1.1 200 OK\r\nSet-Cookie: session=abc; Path=/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var http = new HttpService();
        var request = new RequestModel { Url = Url(listener) };

        var first = ServeAsync(listener, response, Task.CompletedTask);
        await http.SendAsync(request, CancellationToken.None);
        await first;

        var second = ServeAsync(listener, response, Task.CompletedTask);
        await http.SendAsync(request, CancellationToken.None);

        Assert.DoesNotContain("Cookie:", await second, StringComparison.OrdinalIgnoreCase);
    }

    private static string Url(TcpListener listener) =>
        $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

    // Accepts one connection, reads the request head, writes the response and holds the
    // connection open until `hold` completes. Returns the request head.
    private static async Task<string> ServeAsync(TcpListener listener, string response, Task hold)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var head = new StringBuilder();
        var chunk = new byte[4096];
        while (!head.ToString().Contains("\r\n\r\n"))
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0) break;
            head.Append(Encoding.Latin1.GetString(chunk, 0, read));
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        await hold;
        return head.ToString();
    }
}
