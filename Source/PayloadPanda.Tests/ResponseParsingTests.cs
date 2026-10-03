using System.Text;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class ResponseParsingTests
{
    private static ResponseModel ParseRaw(string raw, out ConnectionDiagnostics diag)
    {
        diag = new ConnectionDiagnostics();
        return RawSocketService.ParseResponse(Encoding.Latin1.GetBytes(raw), diag);
    }

    [Fact]
    public void Interim_responses_are_skipped()
    {
        var response = ParseRaw(
            "HTTP/1.1 100 Continue\r\n\r\n" +
            "HTTP/1.1 103 Early Hints\r\nLink: </a.css>\r\n\r\n" +
            "HTTP/1.1 201 Created\r\nContent-Length: 2\r\n\r\nok", out var diag);

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("Created", response.ReasonPhrase);
        Assert.Equal("ok", response.Body);
        Assert.Contains("100 Continue", diag.RawResponseHead);
        Assert.Contains("201 Created", diag.RawResponseHead);
        Assert.Null(response.GetHeader("Link"));
    }

    [Fact]
    public void Repeated_header_lines_are_all_kept()
    {
        var response = ParseRaw(
            "HTTP/1.1 200 OK\r\nSet-Cookie: a=1; Path=/\r\nSet-Cookie: b=2\r\nVary: Origin\r\nVary: Accept-Encoding\r\n\r\n", out _);

        Assert.Equal(["a=1; Path=/", "b=2"], response.Headers.Where(h => h.Key == "Set-Cookie").Select(h => h.Value));
        Assert.Equal("Origin, Accept-Encoding", response.GetHeader("vary"));
    }

    [Fact]
    public void Chunked_bodies_are_decoded()
    {
        var response = ParseRaw(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n4\r\nWiki\r\n5;ext=1\r\npedia\r\n0\r\n\r\n", out _);

        Assert.Equal("Wikipedia", response.Body);
        Assert.Equal(9, response.ResponseSize);
    }

    [Fact]
    public void Body_is_decoded_with_the_declared_charset()
    {
        var response = ParseRaw("HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=iso-8859-1\r\n\r\ncafé", out _);
        Assert.Equal("café", response.Body);
    }
}

public class ResponseTextTests
{
    [Fact]
    public void Utf8_is_the_default_and_a_bom_is_stripped()
    {
        Assert.Equal("é", ResponseText.Decode([0xC3, 0xA9], null));
        Assert.Equal("{}", ResponseText.Decode([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}'], "application/json"));
    }

    [Fact]
    public void Legacy_code_pages_are_supported()
    {
        Assert.Equal("€5", ResponseText.Decode([0x80, (byte)'5'], "text/plain; charset=windows-1252"));
    }

    [Fact]
    public void Unknown_charset_falls_back_to_utf8()
    {
        Assert.Equal("é", ResponseText.Decode([0xC3, 0xA9], "text/plain; charset=made-up"));
    }

    [Fact]
    public async Task Capped_read_truncates_and_reports_it()
    {
        var (bytes, truncated) = await ResponseText.ReadCappedAsync(new MemoryStream(new byte[100]), 64, CancellationToken.None);
        Assert.True(truncated);
        Assert.Equal(64, bytes.Length);

        (bytes, truncated) = await ResponseText.ReadCappedAsync(new MemoryStream(new byte[64]), 64, CancellationToken.None);
        Assert.False(truncated);
        Assert.Equal(64, bytes.Length);
    }
}
