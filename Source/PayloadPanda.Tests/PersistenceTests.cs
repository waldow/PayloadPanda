using System.Text.Json;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class JsonDefaultsTests
{
    [Fact]
    public void Display_formatting_keeps_non_ascii_and_html_characters()
    {
        var formatted = JsonDefaults.TryFormat("{\"name\":\"José 日本\",\"html\":\"<b>a&b</b>\",\"q\":\"it's\"}");

        Assert.Contains("\"name\": \"José 日本\"", formatted);
        Assert.Contains("\"html\": \"<b>a&b</b>\"", formatted);
        Assert.Contains("\"q\": \"it's\"", formatted);
        Assert.DoesNotContain("\\u", formatted);
    }

    [Fact]
    public void Non_json_is_returned_unchanged()
    {
        Assert.Equal("<xml/>", JsonDefaults.TryFormat("<xml/>"));
        Assert.Null(JsonDefaults.TryMinify("not json"));
    }

    [Fact]
    public void Minify_removes_only_insignificant_whitespace()
    {
        Assert.Equal("{\"a\":\"x  y\",\"n\":1.50}", JsonDefaults.TryMinify("{\n  \"a\": \"x  y\",\n  \"n\": 1.50\n}"));
    }

    [Fact]
    public void Enums_are_written_as_names_and_legacy_numbers_still_read()
    {
        var json = JsonSerializer.Serialize(new RequestModel { Method = HttpMethodType.POST, BodyMode = BodyMode.Json }, JsonDefaults.Write);
        Assert.Contains("\"method\": \"POST\"", json);
        Assert.Contains("\"bodyMode\": \"Json\"", json);

        var legacy = JsonSerializer.Deserialize<RequestModel>("{\"method\":1,\"bodyMode\":2,\"authMode\":1}", JsonDefaults.Read)!;
        Assert.Equal(HttpMethodType.POST, legacy.Method);
        Assert.Equal(BodyMode.Json, legacy.BodyMode);
        Assert.Equal(AuthMode.Bearer, legacy.AuthMode);
    }
}

public class HistoryMergeTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Local);

    private static HistoryItem Item(int minutes, string url) => new()
    {
        Timestamp = T0.AddMinutes(minutes),
        Method = HttpMethodType.GET,
        Url = url,
        StatusCode = 200
    };

    [Fact]
    public void Keeps_entries_written_by_another_instance_newest_first()
    {
        var mine = new[] { Item(3, "/c"), Item(1, "/a") };
        var onDisk = new[] { Item(2, "/b"), Item(1, "/a") };

        var merged = PersistenceService.MergeHistory(mine, onDisk, maxItems: 0);

        Assert.Equal(["/c", "/b", "/a"], merged.Select(i => i.Url));
    }

    [Fact]
    public void Trims_to_the_limit_after_merging()
    {
        var merged = PersistenceService.MergeHistory([Item(3, "/c")], [Item(2, "/b"), Item(1, "/a")], maxItems: 2);
        Assert.Equal(["/c", "/b"], merged.Select(i => i.Url));
    }

    [Fact]
    public void Same_instant_in_another_time_zone_is_the_same_entry()
    {
        var local = Item(0, "/a");
        var asUtc = Item(0, "/a");
        asUtc.Timestamp = local.Timestamp.ToUniversalTime();

        Assert.Single(PersistenceService.MergeHistory([local], [asUtc], maxItems: 0));
    }
}

public class RequestSecretsTests
{
    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("cookie", true)]
    [InlineData("X-Auth-Token", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("X-Client-Secret", true)]
    [InlineData("Accept", false)]
    [InlineData("Content-Type", false)]
    public void Detects_credential_headers(string name, bool sensitive)
    {
        Assert.Equal(sensitive, RequestSecrets.IsSensitiveHeaderName(name));
    }

    [Theory]
    [InlineData("api_key", true)]
    [InlineData("access_token", true)]
    [InlineData("key", true)]
    [InlineData("page", false)]
    [InlineData("q", false)]
    public void Detects_credential_query_params(string name, bool sensitive)
    {
        Assert.Equal(sensitive, RequestSecrets.IsSensitiveParamName(name));
    }

    [Fact]
    public void Values_without_the_dpapi_prefix_pass_through()
    {
        Assert.True(SecretProtector.TryUnprotect("plain", out var value));
        Assert.Equal("plain", value);
    }

    [Fact]
    public void Undecryptable_values_come_back_empty_and_are_reported()
    {
        Assert.False(SecretProtector.TryUnprotect("DPAPI:not-base64!", out var value));
        Assert.Equal(string.Empty, value);
    }
}

public class AiImportConversionTests
{
    [Fact]
    public void Unknown_enum_strings_fall_back_to_defaults_and_null_rows_are_dropped()
    {
        var dto = new AiImportResponseDto
        {
            Method = "fetch",
            Url = "https://api.example.com",
            BodyMode = "yaml",
            AuthMode = "bearer",
            AuthToken = "t",
            Headers = [new AiHeaderDto { Key = "X-A", Value = "1" }, null!]
        };

        var request = AiImportService.ConvertDtoToRequest(dto);

        Assert.Equal(HttpMethodType.GET, request.Method);
        Assert.Equal(BodyMode.None, request.BodyMode);
        Assert.Equal(AuthMode.Bearer, request.AuthMode);
        Assert.Single(request.Headers);
        Assert.Equal(RequestComposer.DefaultApiKeyHeader, request.ApiKeyHeader);
    }
}
