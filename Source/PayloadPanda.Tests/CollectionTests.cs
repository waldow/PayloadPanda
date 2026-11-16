using System.Text.Json;
using System.Text.Json.Nodes;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class PostmanCollectionConverterTests
{
    // Shaped like a real troubleshooting collection: string URLs with placeholders,
    // collection variables, a token request whose test script stores the token.
    private const string Sample = """
        {
          "info": {
            "name": "Location troubleshooting (stg)",
            "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
            "description": "Fill the collection variables first."
          },
          "variable": [
            { "key": "baseUrl", "value": "https://api.example.com" },
            { "key": "tokenUrl", "value": "https://login.example.com/oauth/token" },
            { "key": "clientSecret", "value": "" },
            { "key": "machineToken", "value": "" },
            { "key": "crn", "value": 1234567 },
            { "key": "unused", "value": "x", "disabled": true }
          ],
          "item": [
            {
              "name": "0. Get machine token",
              "event": [
                {
                  "listen": "test",
                  "script": {
                    "type": "text/javascript",
                    "exec": [
                      "const body = pm.response.json();",
                      "pm.collectionVariables.set('machineToken', body.access_token);"
                    ]
                  }
                }
              ],
              "request": {
                "method": "POST",
                "header": [{ "key": "Content-Type", "value": "application/json" }],
                "url": "{{tokenUrl}}",
                "body": { "mode": "raw", "raw": "{\n  \"client_secret\": \"{{clientSecret}}\"\n}" }
              }
            },
            {
              "name": "Lookups",
              "item": [
                {
                  "name": "1. Get person",
                  "request": {
                    "method": "GET",
                    "header": [
                      { "key": "Authorization", "value": "Bearer {{machineToken}}" },
                      { "key": "X-Debug", "value": "1", "disabled": true }
                    ],
                    "url": "{{baseUrl}}/api/v1/person/{{crn}}?verbose=true",
                    "description": "Read only."
                  }
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Imports_names_variables_folders_and_requests()
    {
        var (collection, warnings) = PostmanCollectionConverter.Import(Sample);

        Assert.Empty(warnings);
        Assert.Equal("Location troubleshooting (stg)", collection.Name);
        Assert.Equal("Fill the collection variables first.", collection.Description);
        Assert.Equal(["baseUrl", "tokenUrl", "clientSecret", "machineToken", "crn", "unused"], collection.Variables.Select(v => v.Key));
        Assert.Equal("1234567", collection.Variables.Single(v => v.Key == "crn").Value);
        Assert.False(collection.Variables.Single(v => v.Key == "unused").IsEnabled);
        Assert.Equal(["clientSecret", "machineToken"], collection.Variables.Where(v => v.IsSecret).Select(v => v.Key));

        var token = collection.Items[0];
        Assert.False(token.IsFolder);
        Assert.Equal(HttpMethodType.POST, token.Request!.Method);
        Assert.Equal("{{tokenUrl}}", token.Request.Url);
        Assert.Equal(BodyMode.Json, token.Request.BodyMode); // from the Content-Type header
        Assert.Contains("{{clientSecret}}", token.Request.BodyText);
        Assert.Equal(
            "const body = pm.response.json();\npm.collectionVariables.set('machineToken', body.access_token);",
            token.Request.TestScript);

        var folder = collection.Items[1];
        Assert.True(folder.IsFolder);
        var person = Assert.Single(folder.Items);
        Assert.Equal("{{baseUrl}}/api/v1/person/{{crn}}?verbose=true", person.Request!.Url);
        Assert.Equal("Read only.", person.Description);
        Assert.Equal([true, false], person.Request.Headers.Select(h => h.IsEnabled));
        Assert.Equal(RequestModel.CurrentFormatVersion, person.Request.FormatVersion);
    }

    [Fact]
    public void Export_then_import_gives_back_the_same_collection()
    {
        var (original, _) = PostmanCollectionConverter.Import(Sample);
        original.PreRequestScript = "console.log('before');";
        original.Items[1].TestScript = "pm.test('ok', () => {});";

        var exported = PostmanCollectionConverter.Export(original);
        var (roundTripped, warnings) = PostmanCollectionConverter.Import(exported);

        Assert.Empty(warnings);
        Assert.Equal(PostmanCollectionConverter.SchemaUrl, JsonNode.Parse(exported)!["info"]!["schema"]!.GetValue<string>());
        Assert.Equal(Comparable(original), Comparable(roundTripped));
    }

    // Everything except ids and timestamps, which a re-import assigns afresh.
    private static string Comparable(CollectionModel collection)
    {
        var node = JsonSerializer.SerializeToNode(collection, JsonDefaults.Write)!;
        Strip(node);
        return node.ToJsonString();

        static void Strip(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "id", "createdAt", "modifiedAt" })
                    obj.Remove(key);
                foreach (var child in obj.Select(p => p.Value).ToList())
                    Strip(child);
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                    Strip(child);
            }
        }
    }

    [Fact]
    public void Url_objects_bodies_and_auth_map_to_the_request_model()
    {
        const string json = """
            {
              "info": { "name": "Shapes" },
              "auth": { "type": "bearer", "bearer": [{ "key": "token", "value": "{{token}}" }] },
              "item": [
                {
                  "name": "object url",
                  "request": {
                    "method": "PUT",
                    "url": {
                      "raw": "https://api.example.com/users/:id/files?keep=1",
                      "query": [{ "key": "keep", "value": "1" }, { "key": "debug", "value": "true", "disabled": true }],
                      "variable": [{ "key": "id", "value": "42" }]
                    },
                    "body": {
                      "mode": "formdata",
                      "formdata": [
                        { "key": "note", "value": "hi", "type": "text" },
                        { "key": "doc", "type": "file", "src": ["/tmp/a.pdf", "/tmp/b.pdf"] }
                      ]
                    }
                  }
                },
                {
                  "name": "parts only",
                  "request": {
                    "method": "POST",
                    "auth": { "type": "basic", "basic": { "username": "u", "password": "p" } },
                    "url": { "protocol": "https", "host": ["api", "example", "com"], "port": "8443", "path": ["v1", "items"], "query": [{ "key": "a", "value": "b" }] },
                    "body": { "mode": "urlencoded", "urlencoded": [{ "key": "x", "value": "1" }, { "key": "y", "value": "2", "disabled": true }] }
                  }
                },
                {
                  "name": "api key and binary",
                  "request": {
                    "method": "POST",
                    "auth": { "type": "apikey", "apikey": [{ "key": "key", "value": "X-Key" }, { "key": "value", "value": "abc" }, { "key": "in", "value": "header" }] },
                    "url": "https://api.example.com/upload",
                    "body": { "mode": "file", "file": { "src": "/tmp/video.mp4" } }
                  }
                },
                { "name": "no auth", "request": { "method": "GET", "auth": { "type": "noauth" }, "url": "https://api.example.com/open" } }
              ]
            }
            """;

        var (collection, warnings) = PostmanCollectionConverter.Import(json);
        var requests = collection.Items.Select(i => i.Request!).ToList();

        Assert.Empty(warnings);

        Assert.Equal("https://api.example.com/users/42/files?keep=1", requests[0].Url);
        var debug = Assert.Single(requests[0].QueryParams);
        Assert.Equal(("debug", "true", false), (debug.Key, debug.Value, debug.IsEnabled));
        Assert.Equal(AuthMode.Bearer, requests[0].AuthMode); // inherited from the collection
        Assert.Equal("{{token}}", requests[0].AuthToken);
        Assert.Equal(BodyMode.FormData, requests[0].BodyMode);
        Assert.Equal(["", "/tmp/a.pdf", "/tmp/b.pdf"], requests[0].FormFields.Select(f => f.FilePath));
        Assert.Equal([FormFieldKind.Text, FormFieldKind.File, FormFieldKind.File], requests[0].FormFields.Select(f => f.Kind));

        Assert.Equal("https://api.example.com:8443/v1/items?a=b", requests[1].Url);
        Assert.Equal((AuthMode.Basic, "u", "p"), (requests[1].AuthMode, requests[1].AuthUsername, requests[1].AuthPassword));
        Assert.Equal(BodyMode.FormUrlEncoded, requests[1].BodyMode);
        Assert.Equal([true, false], requests[1].FormFields.Select(f => f.IsEnabled));

        Assert.Equal((AuthMode.ApiKey, "X-Key", "abc"), (requests[2].AuthMode, requests[2].ApiKeyHeader, requests[2].ApiKeyValue));
        Assert.Equal((BodyMode.Binary, "/tmp/video.mp4"), (requests[2].BodyMode, requests[2].BinaryFilePath));

        Assert.Equal(AuthMode.None, requests[3].AuthMode);
    }

    [Fact]
    public void What_cannot_be_imported_is_reported()
    {
        const string json = """
            {
              "info": { "name": "Gaps" },
              "item": [
                { "name": "custom verb", "request": { "method": "PROPFIND", "url": "https://dav.example.com/" } },
                { "name": "oauth", "request": { "method": "GET", "auth": { "type": "oauth2" }, "url": "https://api.example.com/" } },
                { "name": "path var", "request": { "method": "GET", "url": { "raw": "https://api.example.com/users/:id", "variable": [{ "key": "id", "value": "" }] } } },
                { "name": "graph", "request": { "method": "POST", "url": "https://api.example.com/graphql", "body": { "mode": "graphql", "graphql": { "query": "{ me { id } }", "variables": "{\"a\":1}" } } },
                  "response": [{ "name": "example", "code": 200 }] }
              ]
            }
            """;

        var (collection, warnings) = PostmanCollectionConverter.Import(json);

        Assert.Equal(HttpMethodType.GET, collection.Items[0].Request!.Method);
        Assert.Contains(warnings, w => w.Contains("PROPFIND"));
        Assert.Contains(warnings, w => w.Contains("oauth2 auth isn't supported"));
        Assert.Contains(warnings, w => w.Contains(":id has no value"));
        Assert.Contains(warnings, w => w.Contains("GraphQL"));
        Assert.Contains(warnings, w => w.Contains("1 saved example response was not imported"));
        Assert.Equal(BodyMode.Json, collection.Items[3].Request!.BodyMode);
        Assert.Equal(1, JsonNode.Parse(collection.Items[3].Request!.BodyText)!["variables"]!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Export_merges_params_rows_and_keeps_placeholders_unencoded()
    {
        var collection = new CollectionModel
        {
            Name = "Out",
            Items =
            [
                new()
                {
                    Name = "enabled rows only",
                    Request = new RequestModel
                    {
                        Url = "{{baseUrl}}/merchants?orgId=old&x=1#top",
                        QueryParams = [new() { Key = "orgId", Value = "{{orgId}}" }]
                    }
                },
                new()
                {
                    Name = "with a disabled row",
                    Request = new RequestModel
                    {
                        Method = HttpMethodType.POST,
                        Url = "https://api.example.com:8443/v1/items?a=b",
                        QueryParams = [new() { Key = "debug", Value = "1", IsEnabled = false }],
                        AuthMode = AuthMode.Bearer,
                        AuthToken = "{{token}}",
                        BodyMode = BodyMode.Json,
                        BodyText = "{}",
                        FollowRedirects = false
                    }
                }
            ]
        };

        var items = JsonNode.Parse(PostmanCollectionConverter.Export(collection))!["item"]!.AsArray();

        Assert.Equal("{{baseUrl}}/merchants?x=1&orgId={{orgId}}#top", items[0]!["request"]!["url"]!.GetValue<string>());

        var url = items[1]!["request"]!["url"]!;
        Assert.Equal("https://api.example.com:8443/v1/items?a=b", url["raw"]!.GetValue<string>());
        Assert.Equal("https", url["protocol"]!.GetValue<string>());
        Assert.Equal(["api", "example", "com"], url["host"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Equal("8443", url["port"]!.GetValue<string>());
        Assert.Equal(["v1", "items"], url["path"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.True(url["query"]![1]!["disabled"]!.GetValue<bool>());
        Assert.Equal("json", items[1]!["request"]!["body"]!["options"]!["raw"]!["language"]!.GetValue<string>());
        Assert.Equal("bearer", items[1]!["request"]!["auth"]!["type"]!.GetValue<string>());
        Assert.False(items[1]!["protocolProfileBehavior"]!["followRedirects"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"name\": \"just some json\"}")]
    public void Anything_that_is_not_a_collection_is_rejected(string json)
    {
        Assert.Throws<FormatException>(() => PostmanCollectionConverter.Import(json));
    }
}

public class VariableResolverTests
{
    private static readonly Dictionary<string, string> Vars = new()
    {
        ["baseUrl"] = "https://api.example.com",
        ["crn"] = "42",
        ["personUrl"] = "{{baseUrl}}/person/{{crn}}",
        ["loop"] = "{{loop}}"
    };

    [Fact]
    public void Known_names_are_replaced_and_unknown_ones_are_left_and_reported()
    {
        var unresolved = new HashSet<string>();

        var text = VariableResolver.Resolve("{{baseUrl}}/a/{{ crn }}/{{missing}}", Vars, unresolved);

        Assert.Equal("https://api.example.com/a/42/{{missing}}", text);
        Assert.Equal(["missing"], unresolved);
    }

    [Fact]
    public void Values_can_refer_to_other_variables_but_not_forever()
    {
        Assert.Equal("https://api.example.com/person/42", VariableResolver.Resolve("{{personUrl}}", Vars));
        Assert.Equal("{{loop}}", VariableResolver.Resolve("{{loop}}", Vars));
    }

    [Fact]
    public void Json_braces_are_not_placeholders()
    {
        const string body = "{\"a\":{\"b\":{}}}";

        Assert.Equal(body, VariableResolver.Resolve(body, Vars));
        Assert.False(VariableResolver.HasPlaceholders(body));
        Assert.True(VariableResolver.HasPlaceholders("{{crn}}"));
    }

    [Fact]
    public void Dynamic_variables_are_generated()
    {
        Assert.True(Guid.TryParse(VariableResolver.Resolve("{{$guid}}", Vars), out _));
        Assert.True(long.TryParse(VariableResolver.Resolve("{{$timestamp}}", Vars), out _));
        Assert.InRange(int.Parse(VariableResolver.Resolve("{{$randomInt}}", Vars)), 0, 1000);
    }

    [Fact]
    public void A_request_is_resolved_everywhere_without_touching_the_original()
    {
        var request = new RequestModel
        {
            Url = "{{baseUrl}}/person/{{crn}}",
            Headers = [new() { Key = "X-Crn", Value = "{{crn}}" }],
            QueryParams = [new() { Key = "q", Value = "{{nope}}" }],
            BodyMode = BodyMode.Json,
            BodyText = "{\"crn\":\"{{crn}}\"}",
            FormFields = [new() { Key = "f", Value = "{{crn}}" }],
            AuthMode = AuthMode.Bearer,
            AuthToken = "{{token}}",
            TestScript = "pm.variables.get('{{crn}}')"
        };

        var (resolved, unresolved) = VariableResolver.ResolveRequest(request, Vars);

        Assert.Equal("https://api.example.com/person/42", resolved.Url);
        Assert.Equal("42", resolved.Headers[0].Value);
        Assert.Equal("{\"crn\":\"42\"}", resolved.BodyText);
        Assert.Equal("42", resolved.FormFields[0].Value);
        Assert.Equal(["nope", "token"], unresolved);
        Assert.Equal("pm.variables.get('{{crn}}')", resolved.TestScript); // scripts are not rewritten
        Assert.Equal("{{baseUrl}}/person/{{crn}}", request.Url);
    }
}

public class CollectionModelTests
{
    private static CollectionModel Tree()
    {
        var request = new CollectionItem { Name = "leaf", Request = new RequestModel { Url = "https://example.com" } };
        var inner = new CollectionItem { Name = "inner", Items = [request] };
        var outer = new CollectionItem { Name = "outer", Items = [inner] };
        return new CollectionModel { Name = "c", Items = [outer, new CollectionItem { Name = "top", Request = new RequestModel() }] };
    }

    [Fact]
    public void Path_lists_the_folders_down_to_the_item()
    {
        var collection = Tree();
        var leaf = collection.AllItems().Single(i => i.Name == "leaf");

        Assert.Equal(["outer", "inner", "leaf"], collection.FindPath(leaf.Id).Select(i => i.Name));
        Assert.Empty(collection.FindPath(Guid.NewGuid()));
        Assert.Same(collection.AllItems().Single(i => i.Name == "inner").Items, collection.FindContainer(leaf.Id));
        Assert.Same(collection.Items, collection.FindContainer(collection.Items[1].Id));
    }

    [Fact]
    public void Clone_is_deep_and_new_ids_reach_every_level()
    {
        var collection = Tree();
        var clone = collection.Clone();
        clone.AllItems().Single(i => i.Name == "leaf").Request!.Url = "changed";

        Assert.Equal("https://example.com", collection.AllItems().Single(i => i.Name == "leaf").Request!.Url);

        var copy = collection.Items[0].CloneWithNewIds();
        var originalIds = collection.AllItems().Select(i => i.Id).ToHashSet();
        Assert.DoesNotContain(copy.Id, originalIds);
        Assert.DoesNotContain(copy.Items[0].Items[0].Id, originalIds);
    }

    [Fact]
    public void Set_variable_updates_the_existing_entry_or_adds_one()
    {
        var collection = new CollectionModel { Variables = [new() { Key = "token", Value = "old", IsEnabled = false }] };

        collection.SetVariable("token", "new");
        collection.SetVariable("other", "1");

        Assert.Equal(new Dictionary<string, string> { ["token"] = "new", ["other"] = "1" }, collection.EnabledVariables());
    }

    [Fact]
    public void Null_filled_files_are_normalized()
    {
        var collection = JsonSerializer.Deserialize<CollectionModel>(
            "{\"name\":null,\"variables\":[null,{\"key\":null}],\"items\":[null,{\"name\":null,\"items\":null,\"request\":{\"headers\":null}}]}",
            JsonDefaults.Read)!;

        collection.Normalize();

        Assert.Equal(string.Empty, collection.Name);
        Assert.Equal(string.Empty, Assert.Single(collection.Variables).Key);
        var item = Assert.Single(collection.Items);
        Assert.Empty(item.Items);
        Assert.Empty(item.Request!.Headers);
    }
}

public class CollectionServiceTests
{
    [Fact]
    public void Plain_values_survive_the_protect_and_unprotect_passes()
    {
        // DPAPI itself is Windows-only; this covers the walk over variables and nested
        // requests with values that need no encryption.
        var collection = new CollectionModel
        {
            Name = "c",
            Variables = [new() { Key = "baseUrl", Value = "https://example.com" }, new() { Key = "empty", Value = "", IsSecret = true }],
            Items = [new() { Name = "folder", Items = [new() { Name = "r", Request = new RequestModel { Url = "{{baseUrl}}/a" } }] }]
        };

        var copy = CollectionService.BuildEncryptedCopy(collection);
        var readable = CollectionService.UnprotectInPlace(copy);

        Assert.True(readable);
        Assert.NotSame(collection.Items[0].Items[0].Request, copy.Items[0].Items[0].Request);
        Assert.Equal("{{baseUrl}}/a", copy.Items[0].Items[0].Request!.Url);
        Assert.Equal("https://example.com", copy.Variables[0].Value);
    }

    [Fact]
    public void An_undecryptable_variable_is_emptied_and_reported()
    {
        var collection = new CollectionModel { Variables = [new() { Key = "token", Value = "DPAPI:not-base64!", IsSecret = true }] };

        Assert.False(CollectionService.UnprotectInPlace(collection));
        Assert.Equal(string.Empty, collection.Variables[0].Value);
    }
}
