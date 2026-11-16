using System.Text.Json.Nodes;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class PostmanEnvironmentConverterTests
{
    private const string Sample = """
        {
          "id": "6f0d6a2e-0000-4000-8000-000000000001",
          "name": "Staging",
          "values": [
            { "key": "baseUrl", "value": "https://stg.example.com", "type": "default", "enabled": true },
            { "key": "clientSecret", "value": "s3cret", "type": "secret", "enabled": true },
            { "key": "apiPassword", "value": "pw" },
            { "key": "retries", "value": 3, "enabled": false },
            { "key": "", "value": "ignored" }
          ],
          "_postman_variable_scope": "environment",
          "_postman_exported_at": "2026-01-01T00:00:00.000Z"
        }
        """;

    [Fact]
    public void Imports_values_with_their_enabled_and_secret_flags()
    {
        var (environment, isGlobals) = PostmanEnvironmentConverter.Import(Sample);

        Assert.False(isGlobals);
        Assert.Equal("Staging", environment.Name);
        Assert.Equal(["baseUrl", "clientSecret", "apiPassword", "retries"], environment.Variables.Select(v => v.Key));
        Assert.Equal([false, true, true, false], environment.Variables.Select(v => v.IsSecret)); // by type, then by name
        Assert.Equal([true, true, true, false], environment.Variables.Select(v => v.IsEnabled));
        Assert.Equal("3", environment.Variables[3].Value);
    }

    [Fact]
    public void Export_then_import_gives_back_the_same_values()
    {
        var (original, _) = PostmanEnvironmentConverter.Import(Sample);

        var exported = PostmanEnvironmentConverter.Export(original);
        var (again, isGlobals) = PostmanEnvironmentConverter.Import(exported);

        Assert.False(isGlobals);
        Assert.Equal("environment", JsonNode.Parse(exported)!["_postman_variable_scope"]!.GetValue<string>());
        Assert.Equal("secret", JsonNode.Parse(exported)!["values"]![1]!["type"]!.GetValue<string>());
        Assert.Equal(
            original.Variables.Select(v => (v.Key, v.Value, v.IsEnabled, v.IsSecret)),
            again.Variables.Select(v => (v.Key, v.Value, v.IsEnabled, v.IsSecret)));
    }

    [Fact]
    public void A_globals_file_is_recognized()
    {
        var exported = PostmanEnvironmentConverter.Export(new EnvironmentModel { Name = "Globals", Variables = [new() { Key = "a", Value = "1" }] }, isGlobals: true);

        var (globals, isGlobals) = PostmanEnvironmentConverter.Import(exported);

        Assert.True(isGlobals);
        Assert.Equal("1", globals.EnabledVariables()["a"]);
    }

    [Theory]
    [InlineData("not json", "valid JSON")]
    [InlineData("{\"name\":\"x\"}", "doesn't look like a Postman environment")]
    [InlineData("{\"info\":{\"name\":\"c\"},\"item\":[]}", "Postman collection, not an environment")]
    public void Other_files_are_rejected_with_a_reason(string json, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => PostmanEnvironmentConverter.Import(json));

        Assert.Contains(expected, ex.Message);
    }
}

public class VariableScopesTests
{
    [Fact]
    public void Merged_applies_globals_then_collection_then_environment_then_locals()
    {
        var scopes = new VariableScopes
        {
            Globals = new Dictionary<string, string> { ["a"] = "global", ["b"] = "global", ["c"] = "global", ["d"] = "global" },
            Collection = new Dictionary<string, string> { ["b"] = "collection", ["c"] = "collection", ["d"] = "collection" },
            Environment = new Dictionary<string, string> { ["c"] = "environment", ["d"] = "environment" },
            Locals = new Dictionary<string, string> { ["d"] = "local" }
        };

        Assert.Equal(
            new Dictionary<string, string> { ["a"] = "global", ["b"] = "collection", ["c"] = "environment", ["d"] = "local" },
            scopes.Merged());
        Assert.Empty(VariableScopes.Empty.Merged());
    }

    [Fact]
    public void An_environment_value_decides_where_a_request_goes()
    {
        var request = new RequestModel { Url = "{{baseUrl}}/person/{{crn}}" };
        var collection = new Dictionary<string, string> { ["baseUrl"] = "https://default.example.com", ["crn"] = "42" };

        string Url(Dictionary<string, string>? environment) => VariableResolver.ResolveRequest(request,
            new VariableScopes { Collection = collection, Environment = environment ?? [] }.Merged()).Request.Url;

        Assert.Equal("https://default.example.com/person/42", Url(null));
        Assert.Equal("https://stg.example.com/person/42", Url(new() { ["baseUrl"] = "https://stg.example.com" }));
    }
}

public class EnvironmentModelTests
{
    [Fact]
    public void Variable_list_helpers_set_remove_and_ignore_disabled_rows()
    {
        var variables = new List<CollectionVariable>
        {
            new() { Key = " token ", Value = "old", IsEnabled = false },
            new() { Key = "off", Value = "x", IsEnabled = false }
        };

        var (existing, addedExisting) = variables.SetValue("token", "new");
        var (_, addedNew) = variables.SetValue("fresh", "1");
        variables.RemoveKey("nothing");

        Assert.False(addedExisting);
        Assert.True(addedNew);
        Assert.True(existing.IsEnabled);
        Assert.Equal(new Dictionary<string, string> { ["token"] = "new", ["fresh"] = "1" }, variables.EnabledMap());

        variables.RemoveKey("token");
        Assert.DoesNotContain(variables, v => v.Key.Trim() == "token");
    }

    [Fact]
    public void Clone_is_deep_and_plain_values_survive_the_protect_passes()
    {
        var environment = new EnvironmentModel
        {
            Name = "stg",
            Variables = [new() { Key = "baseUrl", Value = "https://stg.example.com" }, new() { Key = "empty", Value = "", IsSecret = true }]
        };

        var copy = EnvironmentService.BuildEncryptedCopy(environment);
        copy.Variables[0].Value = "changed";

        Assert.Equal("https://stg.example.com", environment.Variables[0].Value);
        Assert.True(EnvironmentService.UnprotectInPlace(copy));

        var locked = new EnvironmentModel { Variables = [new() { Key = "token", Value = "DPAPI:not-base64!", IsSecret = true }] };
        Assert.False(EnvironmentService.UnprotectInPlace(locked));
        Assert.Equal(string.Empty, locked.Variables[0].Value);
    }
}
