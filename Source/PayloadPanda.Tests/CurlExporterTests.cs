using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class CurlExporterTests
{
    private static RequestModel Post(string body, BodyMode mode = BodyMode.Json) => new()
    {
        Method = HttpMethodType.POST,
        Url = "https://api.example.com/items",
        BodyMode = mode,
        BodyText = body,
        FollowRedirects = false
    };

    private static string Bash(RequestModel request) => CurlExporter.Generate(request, CurlExportStyle.Bash).Command;

    [Fact]
    public void Includes_query_params_from_the_grid()
    {
        var request = new RequestModel
        {
            Url = "https://api.example.com/search",
            QueryParams = [new() { Key = "q", Value = "panda" }, new() { Key = "tag", Value = "a" }, new() { Key = "tag", Value = "b" }]
        };

        Assert.Contains("'https://api.example.com/search?q=panda&tag=a&tag=b'", Bash(request));
    }

    [Fact]
    public void Get_with_leftover_body_text_sends_no_body()
    {
        var request = Post("{\"a\":1}");
        request.Method = HttpMethodType.GET;

        var command = Bash(request);

        Assert.DoesNotContain("--data", command);
        Assert.DoesNotContain("-X", command);
        Assert.DoesNotContain("Content-Type", command);
    }

    [Fact]
    public void Head_uses_dash_I()
    {
        var command = Bash(new RequestModel { Method = HttpMethodType.HEAD, Url = "https://api.example.com/" });

        Assert.StartsWith("curl -I ", command);
        Assert.DoesNotContain("-X HEAD", command);
    }

    [Fact]
    public void Post_has_one_content_type_and_uses_data_raw()
    {
        var request = Post("@not-a-file");
        request.Headers = [new() { Key = "Content-Type", Value = "application/vnd.api+json" }];

        var command = Bash(request);

        Assert.Contains("-X POST", command);
        Assert.Single(command.Split('\n'), line => line.Contains("Content-Type"));
        Assert.Contains("-H 'Content-Type: application/vnd.api+json'", command);
        Assert.Contains("--data-raw '@not-a-file'", command);
    }

    [Fact]
    public void Basic_auth_is_exported_as_dash_u()
    {
        var request = new RequestModel
        {
            Url = "https://api.example.com/",
            AuthMode = AuthMode.Basic,
            AuthUsername = "user",
            AuthPassword = "pw"
        };

        var command = Bash(request);

        Assert.Contains("-u 'user:pw'", command);
        Assert.DoesNotContain("Authorization", command);
    }

    [Fact]
    public void Empty_header_value_uses_curl_semicolon_syntax()
    {
        var request = new RequestModel { Url = "https://api.example.com/", Headers = [new() { Key = "X-Empty", Value = "" }] };
        Assert.Contains("-H 'X-Empty;'", Bash(request));
    }

    [Fact]
    public void Line_continuations_match_the_shell()
    {
        var request = Post("{}");
        Assert.Contains(" \\\n  ", CurlExporter.Generate(request, CurlExportStyle.Bash).Command);
        Assert.Contains(" `\n  ", CurlExporter.Generate(request, CurlExportStyle.PowerShell).Command);
        Assert.Contains(" ^\n  ", CurlExporter.Generate(request, CurlExportStyle.Cmd).Command);
        Assert.StartsWith("curl.exe ", CurlExporter.Generate(request, CurlExportStyle.WindowsPowerShell).Command);
    }

    [Theory]
    [InlineData(CurlExportStyle.Bash, "it's", "'it'\\''s'")]
    [InlineData(CurlExportStyle.PowerShell, "it's", "'it''s'")]
    [InlineData(CurlExportStyle.PowerShell, "don\u2019t $x", "'don\u2019\u2019t $x'")]
    [InlineData(CurlExportStyle.WindowsPowerShell, "{\"a\": 1}", "'\"{\"\"a\"\": 1}\"'")]
    [InlineData(CurlExportStyle.WindowsPowerShell, "it's 100%", "'\"it''s 100%\"'")]
    [InlineData(CurlExportStyle.Cmd, "{\"q\":\"a&b|c\"}", "\"{\"\"q\"\":\"\"a&b|c\"\"}\"")]
    [InlineData(CurlExportStyle.Cmd, "50%PATH%", "\"50\"%\"PATH\"%\"\"")]
    [InlineData(CurlExportStyle.Cmd, "C:\\dir\\", "\"C:\\dir\\\\\"")]
    public void Quotes_values_for_each_shell(CurlExportStyle style, string value, string expected)
    {
        Assert.Equal(expected, CurlExporter.Quote(value, style));
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\"\"b\"")]     // backslash before a quote is doubled
    [InlineData("C:\\dir\\", "\"C:\\dir\\\\\"")] // so is a trailing one, before the closing quote
    [InlineData("a\\b", "\"a\\b\"")]             // backslashes elsewhere stay single
    [InlineData("100%", "\"100%\"")]                // % only needs protecting from cmd.exe
    public void Quotes_for_the_c_runtime_that_parses_curl_exe_arguments(string value, string expected)
    {
        Assert.Equal(expected, CurlExporter.QuoteForCRuntime(value, protectPercentFromCmd: false));
    }

    [Fact]
    public void Cmd_minifies_multi_line_json_bodies()
    {
        var result = CurlExporter.Generate(Post("{\n  \"name\": \"José & co\",\n  \"n\": 1\n}"), CurlExportStyle.Cmd);

        Assert.Null(result.Warning);
        Assert.Contains("--data-raw \"{\"\"name\"\":\"\"José & co\"\",\"\"n\"\":1}\"", result.Command);
    }

    [Fact]
    public void Cmd_warns_when_line_breaks_in_a_text_body_are_replaced()
    {
        var result = CurlExporter.Generate(Post("line one\nline two", BodyMode.Raw), CurlExportStyle.Cmd);

        Assert.NotNull(result.Warning);
        Assert.Contains("--data-raw \"line one line two\"", result.Command);
    }

    [Fact]
    public void Multi_line_bodies_stay_intact_for_bash_and_powershell()
    {
        var request = Post("{\n  \"a\": 1\n}");
        Assert.Contains("--data-raw '{\n  \"a\": 1\n}'", Bash(request));
        Assert.Contains("--data-raw '{\n  \"a\": 1\n}'", CurlExporter.Generate(request, CurlExportStyle.PowerShell).Command);
    }

    [Fact]
    public void Exports_the_timeout_and_redirect_options()
    {
        var request = new RequestModel { Url = "https://api.example.com/", TimeoutSeconds = 12, FollowRedirects = true };
        Assert.StartsWith("curl -L --max-time 12 'https://api.example.com/'", Bash(request));
    }
}
