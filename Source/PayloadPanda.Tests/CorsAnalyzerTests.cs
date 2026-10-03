using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class CorsAnalyzerTests
{
    private const string Origin = "https://app.example.com";

    private static ResponseModel Response(params (string Key, string Value)[] headers) => new()
    {
        StatusCode = 204,
        ReasonPhrase = "No Content",
        Headers = headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList()
    };

    private static CorsCheck Check(CorsAnalysisResult result, string label) => Assert.Single(result.Checks, c => c.Label == label);

    [Fact]
    public void Allow_headers_sent_as_several_lines_are_combined()
    {
        var response = Response(
            ("Access-Control-Allow-Origin", Origin),
            ("Access-Control-Allow-Methods", "PUT"),
            ("Access-Control-Allow-Headers", "Content-Type"),
            ("Access-Control-Allow-Headers", "X-Trace"));

        var result = CorsAnalyzer.Analyze(Origin, "PUT", "content-type, x-trace", false, isPreflight: true, response);

        Assert.True(result.Passed, string.Join("; ", result.Checks.Select(c => c.Detail)));
    }

    [Fact]
    public void Wildcard_allow_headers_never_covers_authorization()
    {
        var response = Response(
            ("Access-Control-Allow-Origin", "*"),
            ("Access-Control-Allow-Methods", "*"),
            ("Access-Control-Allow-Headers", "*"));

        var result = CorsAnalyzer.Analyze(Origin, "PUT", "authorization, x-trace", false, isPreflight: true, response);

        var headers = Check(result, "Access-Control-Allow-Headers");
        Assert.Equal(CorsCheckStatus.Fail, headers.Status);
        Assert.Contains("authorization", headers.Detail);
        Assert.DoesNotContain("x-trace", headers.Detail);
        Assert.Equal(CorsCheckStatus.Pass, Check(result, "Access-Control-Allow-Methods").Status);
    }

    [Fact]
    public void Wildcards_are_literal_for_credentialed_requests()
    {
        var response = Response(
            ("Access-Control-Allow-Origin", Origin),
            ("Access-Control-Allow-Credentials", "true"),
            ("Access-Control-Allow-Methods", "*"),
            ("Access-Control-Allow-Headers", "*"));

        var result = CorsAnalyzer.Analyze(Origin, "PUT", "x-trace", includeCredentials: true, isPreflight: true, response);

        Assert.False(result.Passed);
        Assert.Equal(CorsCheckStatus.Fail, Check(result, "Access-Control-Allow-Methods").Status);
        Assert.Equal(CorsCheckStatus.Fail, Check(result, "Access-Control-Allow-Headers").Status);
    }

    [Theory]
    [InlineData("POST", CorsCheckStatus.Pass)]
    [InlineData("GET", CorsCheckStatus.Pass)]
    [InlineData("DELETE", CorsCheckStatus.Fail)]
    public void Safelisted_methods_need_not_be_listed(string method, CorsCheckStatus expected)
    {
        var response = Response(
            ("Access-Control-Allow-Origin", Origin),
            ("Access-Control-Allow-Methods", "PUT"));

        var result = CorsAnalyzer.Analyze(Origin, method, "", false, isPreflight: true, response);

        Assert.Equal(expected, Check(result, "Access-Control-Allow-Methods").Status);
    }

    [Fact]
    public void Origin_comparison_is_case_sensitive()
    {
        var response = Response(("Access-Control-Allow-Origin", "https://App.Example.com"));

        var result = CorsAnalyzer.Analyze(Origin, "GET", "", false, isPreflight: false, response);

        Assert.False(result.Passed);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", false)]
    public void Allow_credentials_must_be_exactly_true(string value, bool passes)
    {
        var response = Response(
            ("Access-Control-Allow-Origin", Origin),
            ("Access-Control-Allow-Credentials", value));

        var result = CorsAnalyzer.Analyze(Origin, "GET", "", includeCredentials: true, isPreflight: false, response);

        Assert.Equal(passes, result.Passed);
    }

    [Fact]
    public void Wildcard_origin_with_credentials_fails()
    {
        var response = Response(
            ("Access-Control-Allow-Origin", "*"),
            ("Access-Control-Allow-Credentials", "true"));

        var result = CorsAnalyzer.Analyze(Origin, "GET", "", includeCredentials: true, isPreflight: false, response);

        Assert.Equal(CorsCheckStatus.Fail, Check(result, "Credentials + wildcard").Status);
    }
}
