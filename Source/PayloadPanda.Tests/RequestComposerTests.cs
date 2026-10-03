using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class RequestComposerTests
{
    private static RequestModel Request(string url = "https://api.example.com/items") => new() { Url = url };

    private static string? Header(ComposedRequest composed, string name) => composed.GetHeader(name);

    [Fact]
    public void Repeated_grid_keys_are_all_sent_in_grid_order()
    {
        var request = Request();
        request.QueryParams =
        [
            new() { Key = "tag", Value = "a" },
            new() { Key = "tag", Value = "b" }
        ];

        Assert.Equal("/items?tag=a&tag=b", RequestComposer.BuildUri(request).PathAndQuery);
    }

    [Fact]
    public void Grid_param_replaces_same_named_url_param_and_leaves_the_rest_as_typed()
    {
        var request = Request("https://api.example.com/items?page=1&q=a%2Cb#top");
        request.QueryParams = [new() { Key = "page", Value = "2" }];

        var uri = RequestComposer.BuildUri(request);

        Assert.Equal("/items?q=a%2Cb&page=2", uri.PathAndQuery);
        Assert.Equal("#top", uri.Fragment);
    }

    [Fact]
    public void Grid_values_are_percent_encoded_and_disabled_or_blank_rows_ignored()
    {
        var request = Request();
        request.QueryParams =
        [
            new() { Key = "q", Value = "a b&c=d" },
            new() { Key = "off", Value = "x", IsEnabled = false },
            new() { Key = "  ", Value = "blank" }
        ];

        Assert.Equal("/items?q=a%20b%26c%3Dd", RequestComposer.BuildUri(request).PathAndQuery);
    }

    [Fact]
    public void Url_without_grid_params_is_used_unchanged()
    {
        var uri = RequestComposer.BuildUri(Request("https://api.example.com/a?x=1&x=2"));
        Assert.Equal("/a?x=1&x=2", uri.PathAndQuery);
    }

    [Fact]
    public void Content_type_row_overrides_the_body_mode_default()
    {
        var request = Request();
        request.Method = HttpMethodType.PATCH;
        request.BodyMode = BodyMode.Json;
        request.BodyText = "[]";
        request.Headers = [new() { Key = "content-type", Value = "application/json-patch+json" }];

        var composed = RequestComposer.Compose(request, includeClientDefaults: false);

        Assert.Single(composed.Headers, h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("application/json-patch+json", Header(composed, "Content-Type"));
    }

    [Theory]
    [InlineData(BodyMode.Json, "application/json")]
    [InlineData(BodyMode.Xml, "application/xml")]
    [InlineData(BodyMode.FormUrlEncoded, "application/x-www-form-urlencoded")]
    [InlineData(BodyMode.Raw, "text/plain")]
    public void Body_mode_sets_the_default_content_type(BodyMode mode, string expected)
    {
        var request = Request();
        request.Method = HttpMethodType.POST;
        request.BodyMode = mode;
        request.BodyText = "x";

        var composed = RequestComposer.Compose(request, includeClientDefaults: false);

        Assert.True(composed.HasBody);
        Assert.Equal(expected, Header(composed, "Content-Type"));
        Assert.Equal("x"u8.ToArray(), composed.Body);
    }

    [Theory]
    [InlineData(HttpMethodType.GET)]
    [InlineData(HttpMethodType.HEAD)]
    public void Get_and_head_never_carry_a_body(HttpMethodType method)
    {
        var request = Request();
        request.Method = method;
        request.BodyMode = BodyMode.Json;
        request.BodyText = "{\"a\":1}";

        var composed = RequestComposer.Compose(request, includeClientDefaults: false);

        Assert.False(composed.HasBody);
        Assert.Empty(composed.Body);
        Assert.Null(Header(composed, "Content-Type"));
    }

    [Fact]
    public void Method_override_decides_whether_the_body_is_sent()
    {
        var request = Request();
        request.Method = HttpMethodType.POST;
        request.BodyMode = BodyMode.Json;
        request.BodyText = "{}";

        Assert.False(RequestComposer.Compose(request, false, methodOverride: "GET").HasBody);
        Assert.True(RequestComposer.Compose(request, false, methodOverride: "PROPFIND").HasBody);
    }

    [Fact]
    public void Auth_tab_replaces_a_same_named_header_row()
    {
        var request = Request();
        request.Headers = [new() { Key = "Authorization", Value = "Bearer stale" }];
        request.AuthMode = AuthMode.Bearer;
        request.AuthToken = "fresh";

        var composed = RequestComposer.Compose(request, includeClientDefaults: false);

        Assert.Single(composed.Headers, h => h.Key == "Authorization");
        Assert.Equal("Bearer fresh", Header(composed, "Authorization"));
    }

    [Fact]
    public void Basic_and_api_key_auth_build_their_headers()
    {
        var basic = Request();
        basic.AuthMode = AuthMode.Basic;
        basic.AuthUsername = "user";
        basic.AuthPassword = "p:ss";
        Assert.Equal("Basic dXNlcjpwOnNz", Header(RequestComposer.Compose(basic, false), "Authorization"));

        var apiKey = Request();
        apiKey.AuthMode = AuthMode.ApiKey;
        apiKey.ApiKeyHeader = "";
        apiKey.ApiKeyValue = "k123";
        Assert.Equal("k123", Header(RequestComposer.Compose(apiKey, false), "X-API-Key"));
    }

    [Fact]
    public void Client_defaults_are_added_only_when_missing()
    {
        var request = Request();
        request.Headers = [new() { Key = "User-Agent", Value = "mine/2.0" }];

        var withDefaults = RequestComposer.Compose(request, includeClientDefaults: true);
        Assert.Equal("mine/2.0", Header(withDefaults, "User-Agent"));
        Assert.Equal("*/*", Header(withDefaults, "Accept"));

        var withoutDefaults = RequestComposer.Compose(Request(), includeClientDefaults: false);
        Assert.Null(Header(withoutDefaults, "User-Agent"));
        Assert.Null(Header(withoutDefaults, "Accept"));
    }

    [Fact]
    public void Content_length_rows_are_dropped_and_disabled_rows_skipped()
    {
        var request = Request();
        request.Headers =
        [
            new() { Key = "Content-Length", Value = "999" },
            new() { Key = "X-Off", Value = "1", IsEnabled = false },
            new() { Key = " X-Trimmed ", Value = "v" }
        ];

        var composed = RequestComposer.Compose(request, includeClientDefaults: false);

        Assert.Equal([new("X-Trimmed", "v")], composed.Headers);
    }

    [Fact]
    public void Line_break_in_a_header_value_is_rejected()
    {
        var request = Request();
        request.Headers = [new() { Key = "X-Test", Value = "a\r\nInjected: yes" }];

        var ex = Assert.Throws<FormatException>(() => RequestComposer.Compose(request, false));
        Assert.Contains("X-Test", ex.Message);
    }

    [Fact]
    public void Invalid_header_name_is_rejected()
    {
        var request = Request();
        request.Headers = [new() { Key = "X Custom", Value = "1" }];

        Assert.Throws<FormatException>(() => RequestComposer.Compose(request, false));
    }

    [Fact]
    public void Relative_url_is_rejected()
    {
        Assert.Throws<UriFormatException>(() => RequestComposer.Compose(Request("api.example.com/x"), false));
    }
}
