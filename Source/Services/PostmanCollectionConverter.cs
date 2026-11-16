using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public sealed record PostmanImportResult(CollectionModel Collection, List<string> Warnings);

/// <summary>
/// Reads and writes Postman Collection v2.1 files (v2.0 reads too: it differs only in how
/// auth parameters are laid out). Parsing is lenient because most Postman fields come in
/// more than one shape (a URL is a string or an object, a description likewise, …).
/// Whatever has no PayloadPanda equivalent is reported as a warning instead of silently lost.
/// </summary>
public static partial class PostmanCollectionConverter
{
    public const string SchemaUrl = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    private static readonly JsonSerializerOptions s_exportOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ==================== Import ====================

    /// <exception cref="FormatException">The text isn't JSON or isn't a Postman collection.</exception>
    public static PostmanImportResult Import(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The file isn't valid JSON: {ex.Message}", ex);
        }

        // The Postman API wraps the collection: { "collection": { ... } }.
        if (root?["collection"] is JsonObject wrapped)
            root = wrapped;
        if (root is null || (root["info"] is not JsonObject && root["item"] is not JsonArray))
            throw new FormatException("This doesn't look like a Postman collection (no \"info\" or \"item\").");

        var warnings = new List<string>();
        var info = root["info"] as JsonObject;
        var (preRequest, test) = ReadScripts(root["event"]);
        var collection = new CollectionModel
        {
            Name = Str(info?["name"]) is { Length: > 0 } name ? name : "Imported collection",
            Description = ReadDescription(info?["description"]),
            PreRequestScript = preRequest,
            TestScript = test,
            Variables = ReadVariables(root["variable"])
        };

        var context = new ImportContext(warnings);
        collection.Items = ReadItems(root["item"] as JsonArray, root["auth"] as JsonObject, context);
        if (context.ExampleResponses > 0)
            warnings.Add($"{context.ExampleResponses} saved example response{(context.ExampleResponses == 1 ? " was" : "s were")} not imported.");

        collection.Normalize();
        return new PostmanImportResult(collection, warnings);
    }

    private sealed class ImportContext(List<string> warnings)
    {
        public List<string> Warnings { get; } = warnings;
        public int ExampleResponses { get; set; }
    }

    private static List<CollectionVariable> ReadVariables(JsonNode? node)
    {
        var variables = new List<CollectionVariable>();
        foreach (var entry in Objects(node))
        {
            var key = Str(entry["key"]) ?? Str(entry["id"]) ?? string.Empty;
            if (key.Length == 0)
                continue;
            variables.Add(new CollectionVariable
            {
                Key = key,
                Value = Str(entry["value"]) ?? string.Empty,
                IsEnabled = !Flag(entry["disabled"]),
                IsSecret = Str(entry["type"]) == "secret" || LooksSecret(key)
            });
        }
        return variables;
    }

    // "machineToken" and "clientSecret" hold credentials; "tokenUrl" only says where to get one.
    internal static bool LooksSecret(string variableName) =>
        RequestSecrets.IsSensitiveParamName(variableName) &&
        !new[] { "url", "uri", "endpoint", "host" }.Any(suffix => variableName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static List<CollectionItem> ReadItems(JsonArray? items, JsonObject? inheritedAuth, ImportContext context)
    {
        var result = new List<CollectionItem>();
        foreach (var entry in Objects(items))
        {
            var name = Str(entry["name"]) ?? string.Empty;
            var (preRequest, test) = ReadScripts(entry["event"]);

            if (entry["item"] is JsonArray children)
            {
                result.Add(new CollectionItem
                {
                    Name = name.Length > 0 ? name : "Folder",
                    Description = ReadDescription(entry["description"]),
                    PreRequestScript = preRequest,
                    TestScript = test,
                    Items = ReadItems(children, entry["auth"] as JsonObject ?? inheritedAuth, context)
                });
                continue;
            }

            if (entry["response"] is JsonArray { Count: > 0 } examples)
                context.ExampleResponses += examples.Count;

            var label = name.Length > 0 ? name : "Request";
            var request = ReadRequest(entry["request"], inheritedAuth, label, context.Warnings);
            request.PreRequestScript = preRequest;
            request.TestScript = test;
            if (entry["protocolProfileBehavior"] is JsonObject behavior && behavior["followRedirects"] is JsonValue follow &&
                follow.TryGetValue<bool>(out var followRedirects))
            {
                request.FollowRedirects = followRedirects;
            }

            var description = ReadDescription(entry["description"]);
            if (description.Length == 0 && entry["request"] is JsonObject requestObject)
                description = ReadDescription(requestObject["description"]);

            result.Add(new CollectionItem { Name = label, Description = description, Request = request });
        }
        return result;
    }

    private static RequestModel ReadRequest(JsonNode? node, JsonObject? inheritedAuth, string label, List<string> warnings)
    {
        var request = new RequestModel { FormatVersion = RequestModel.CurrentFormatVersion };

        // A request can be just its URL.
        if (node is not JsonObject source)
        {
            request.Url = Str(node) ?? string.Empty;
            return request;
        }

        var method = Str(source["method"]) ?? "GET";
        if (Enum.TryParse<HttpMethodType>(method, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) &&
            !char.IsDigit(method.Trim()[0]))
        {
            request.Method = parsed;
        }
        else
        {
            warnings.Add($"\"{label}\": method {method} isn't supported; it was imported as GET.");
        }

        ReadUrl(source["url"], request, label, warnings);
        request.Headers = ReadHeaders(source["header"]);
        ReadBody(source["body"] as JsonObject, request, label, warnings);
        ReadAuth(source["auth"] as JsonObject ?? inheritedAuth, request, label, warnings);
        return request;
    }

    private static void ReadUrl(JsonNode? node, RequestModel request, string label, List<string> warnings)
    {
        if (node is not JsonObject url)
        {
            request.Url = Str(node) ?? string.Empty;
            return;
        }

        var queryEntries = Objects(url["query"]).ToList();
        var raw = Str(url["raw"]);
        if (string.IsNullOrEmpty(raw))
        {
            // No raw form: assemble it from the parts.
            var protocol = Str(url["protocol"]);
            var host = JoinParts(url["host"], ".");
            var port = Str(url["port"]);
            var path = JoinParts(url["path"], "/");
            var query = string.Join('&', queryEntries
                .Where(q => !Flag(q["disabled"]))
                .Select(q => Str(q["value"]) is { } value ? $"{Str(q["key"])}={value}" : Str(q["key"])));
            raw = (string.IsNullOrEmpty(protocol) ? string.Empty : protocol + "://") + host +
                  (string.IsNullOrEmpty(port) ? string.Empty : ":" + port) +
                  (path.Length > 0 ? "/" + path : string.Empty) +
                  (query.Length > 0 ? "?" + query : string.Empty);
        }

        // Path variables (":id") get their value written into the URL.
        foreach (var variable in Objects(url["variable"]))
        {
            var key = Str(variable["key"]) ?? Str(variable["id"]);
            if (string.IsNullOrEmpty(key))
                continue;
            var value = Str(variable["value"]);
            var segment = new Regex($@"(?<=/):{Regex.Escape(key)}(?=/|\?|#|$)");
            if (string.IsNullOrEmpty(value))
            {
                if (segment.IsMatch(raw))
                    warnings.Add($"\"{label}\": path variable :{key} has no value; replace it in the URL.");
                continue;
            }
            raw = segment.Replace(raw, value.Replace("$", "$$"));
        }

        request.Url = raw;

        // Enabled query parameters are already in the URL; only switched-off ones need a row.
        foreach (var query in queryEntries.Where(q => Flag(q["disabled"])))
        {
            var key = Str(query["key"]);
            if (!string.IsNullOrEmpty(key))
                request.QueryParams.Add(new QueryParamData { Key = key, Value = Str(query["value"]) ?? string.Empty, IsEnabled = false });
        }
    }

    private static List<HeaderItemData> ReadHeaders(JsonNode? node)
    {
        var headers = new List<HeaderItemData>();

        // v2.0 allows a "Name: value" block instead of a list.
        if (node is JsonValue && Str(node) is { } block)
        {
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                    headers.Add(new HeaderItemData { Key = line[..colon].Trim(), Value = line[(colon + 1)..].Trim() });
            }
            return headers;
        }

        foreach (var entry in Objects(node))
        {
            var key = Str(entry["key"]);
            if (!string.IsNullOrEmpty(key))
                headers.Add(new HeaderItemData { Key = key, Value = Str(entry["value"]) ?? string.Empty, IsEnabled = !Flag(entry["disabled"]) });
        }
        return headers;
    }

    private static void ReadBody(JsonObject? body, RequestModel request, string label, List<string> warnings)
    {
        if (body is null || Flag(body["disabled"]))
            return;

        switch (Str(body["mode"]))
        {
            case "raw":
                request.BodyText = Str(body["raw"]) ?? string.Empty;
                var language = Str((body["options"] as JsonObject)?["raw"]?["language"]);
                var contentType = request.Headers
                    .FirstOrDefault(h => h.IsEnabled && h.Key.Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
                request.BodyMode = (language, contentType) switch
                {
                    ("json", _) => BodyMode.Json,
                    ("xml", _) => BodyMode.Xml,
                    (null or "text", { } type) when type.Contains("json", StringComparison.OrdinalIgnoreCase) => BodyMode.Json,
                    (null or "text", { } type) when type.Contains("xml", StringComparison.OrdinalIgnoreCase) => BodyMode.Xml,
                    _ => BodyMode.Raw
                };
                // Postman sends these two languages with their own Content-Type.
                if (contentType is null && language is "javascript" or "html")
                {
                    request.Headers.Add(new HeaderItemData
                    {
                        Key = "Content-Type",
                        Value = language == "html" ? "text/html" : "application/javascript"
                    });
                }
                break;

            case "urlencoded":
                request.BodyMode = BodyMode.FormUrlEncoded;
                foreach (var field in Objects(body["urlencoded"]))
                {
                    request.FormFields.Add(new FormFieldData
                    {
                        Key = Str(field["key"]) ?? string.Empty,
                        Value = Str(field["value"]) ?? string.Empty,
                        IsEnabled = !Flag(field["disabled"])
                    });
                }
                break;

            case "formdata":
                request.BodyMode = BodyMode.FormData;
                foreach (var field in Objects(body["formdata"]))
                {
                    var key = Str(field["key"]) ?? string.Empty;
                    var enabled = !Flag(field["disabled"]);
                    var partType = Str(field["contentType"]) ?? string.Empty;
                    if (Str(field["type"]) != "file")
                    {
                        request.FormFields.Add(new FormFieldData { Key = key, Value = Str(field["value"]) ?? string.Empty, IsEnabled = enabled, ContentType = partType });
                        continue;
                    }

                    // "src" is one path or a list of them; each file becomes its own row.
                    var paths = field["src"] is JsonArray list
                        ? list.Select(Str).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList()
                        : new List<string> { Str(field["src"]) ?? string.Empty };
                    if (paths.Count == 0)
                        paths.Add(string.Empty);
                    foreach (var path in paths)
                        request.FormFields.Add(new FormFieldData { Key = key, Kind = FormFieldKind.File, FilePath = path, IsEnabled = enabled, ContentType = partType });
                }
                break;

            case "file":
                request.BodyMode = BodyMode.Binary;
                request.BinaryFilePath = Str((body["file"] as JsonObject)?["src"]) ?? string.Empty;
                break;

            case "graphql":
                var graphql = body["graphql"] as JsonObject;
                var payload = new JsonObject { ["query"] = Str(graphql?["query"]) ?? string.Empty };
                var variables = Str(graphql?["variables"]);
                if (!string.IsNullOrWhiteSpace(variables))
                {
                    try { payload["variables"] = JsonNode.Parse(variables); }
                    catch (JsonException) { payload["variables"] = variables; }
                }
                request.BodyMode = BodyMode.Json;
                request.BodyText = payload.ToJsonString(s_exportOptions);
                warnings.Add($"\"{label}\": the GraphQL body was imported as a JSON body.");
                break;

            case null or "" or "none":
                break;

            case var other:
                warnings.Add($"\"{label}\": body mode \"{other}\" isn't supported; the body was left out.");
                break;
        }
    }

    private static void ReadAuth(JsonObject? auth, RequestModel request, string label, List<string> warnings)
    {
        var type = Str(auth?["type"]);
        switch (type)
        {
            case null or "" or "noauth":
                break;
            case "bearer":
                request.AuthMode = AuthMode.Bearer;
                request.AuthToken = AuthParam(auth!, "bearer", "token");
                break;
            case "basic":
                request.AuthMode = AuthMode.Basic;
                request.AuthUsername = AuthParam(auth!, "basic", "username");
                request.AuthPassword = AuthParam(auth!, "basic", "password");
                break;
            case "apikey":
                var key = AuthParam(auth!, "apikey", "key");
                var value = AuthParam(auth!, "apikey", "value");
                if (AuthParam(auth!, "apikey", "in") == "query")
                {
                    request.QueryParams.Add(new QueryParamData { Key = key, Value = value });
                }
                else
                {
                    request.AuthMode = AuthMode.ApiKey;
                    request.ApiKeyHeader = key;
                    request.ApiKeyValue = value;
                }
                break;
            default:
                warnings.Add($"\"{label}\": {type} auth isn't supported; set the credentials up by hand.");
                break;
        }
    }

    // v2.1 lists auth parameters as [{ "key": ..., "value": ... }]; v2.0 uses a plain object.
    private static string AuthParam(JsonObject auth, string type, string name)
    {
        var parameters = auth[type];
        if (parameters is JsonObject map)
            return Str(map[name]) ?? string.Empty;
        return Str(Objects(parameters).FirstOrDefault(p => Str(p["key"]) == name)?["value"]) ?? string.Empty;
    }

    private static (string PreRequest, string Test) ReadScripts(JsonNode? events)
    {
        var preRequest = new List<string>();
        var test = new List<string>();
        foreach (var entry in Objects(events))
        {
            if (Flag(entry["disabled"]) || entry["script"] is not JsonObject script)
                continue;

            var code = script["exec"] is JsonArray lines
                ? string.Join('\n', lines.Select(l => Str(l) ?? string.Empty))
                : Str(script["exec"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
                continue;

            switch (Str(entry["listen"]))
            {
                case "prerequest": preRequest.Add(code); break;
                case "test": test.Add(code); break;
            }
        }
        return (string.Join('\n', preRequest), string.Join('\n', test));
    }

    private static string ReadDescription(JsonNode? node) =>
        node is JsonObject described ? Str(described["content"]) ?? string.Empty : Str(node) ?? string.Empty;

    // ==================== Export ====================

    /// <summary>The collection as Postman v2.1 JSON. Secrets are written in plain text, like exported requests.</summary>
    public static string Export(CollectionModel collection)
    {
        var info = new JsonObject
        {
            ["_postman_id"] = collection.Id.ToString(),
            ["name"] = collection.Name,
            ["schema"] = SchemaUrl
        };
        if (collection.Description.Length > 0)
            info["description"] = collection.Description;

        var root = new JsonObject
        {
            ["info"] = info,
            ["item"] = WriteItems(collection.Items)
        };
        if (WriteEvents(collection.PreRequestScript, collection.TestScript) is { } events)
            root["event"] = events;

        var variables = new JsonArray();
        foreach (var variable in collection.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Key)))
        {
            var entry = new JsonObject { ["key"] = variable.Key, ["value"] = variable.Value, ["type"] = "string" };
            if (!variable.IsEnabled)
                entry["disabled"] = true;
            variables.Add(entry);
        }
        if (variables.Count > 0)
            root["variable"] = variables;

        return root.ToJsonString(s_exportOptions);
    }

    private static JsonArray WriteItems(IEnumerable<CollectionItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var entry = new JsonObject { ["name"] = item.Name };
            if (item.Request is null)
            {
                if (item.Description.Length > 0)
                    entry["description"] = item.Description;
                entry["item"] = WriteItems(item.Items);
                if (WriteEvents(item.PreRequestScript, item.TestScript) is { } folderEvents)
                    entry["event"] = folderEvents;
            }
            else
            {
                if (WriteEvents(item.Request.PreRequestScript, item.Request.TestScript) is { } events)
                    entry["event"] = events;
                if (!item.Request.FollowRedirects)
                    entry["protocolProfileBehavior"] = new JsonObject { ["followRedirects"] = false };
                entry["request"] = WriteRequest(item.Request, item.Description);
                entry["response"] = new JsonArray();
            }
            array.Add(entry);
        }
        return array;
    }

    private static JsonObject WriteRequest(RequestModel request, string description)
    {
        var headers = new JsonArray();
        foreach (var header in request.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Key)))
        {
            var entry = new JsonObject { ["key"] = header.Key, ["value"] = header.Value };
            if (!header.IsEnabled)
                entry["disabled"] = true;
            headers.Add(entry);
        }

        var result = new JsonObject { ["method"] = request.Method.ToString(), ["header"] = headers };
        if (WriteAuth(request) is { } auth)
            result["auth"] = auth;
        if (WriteBody(request) is { } body)
            result["body"] = body;
        result["url"] = WriteUrl(request);
        if (description.Length > 0)
            result["description"] = description;
        return result;
    }

    // The URL keeps its placeholders, so nothing here is percent-encoded: enabled Params
    // rows are appended as typed (replacing same-named parameters in the URL, as when
    // sending). A plain string unless there are switched-off rows, which need the object form.
    private static JsonNode WriteUrl(RequestModel request)
    {
        var url = request.Url.Trim();
        var enabled = request.QueryParams.Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key)).ToList();
        var disabled = request.QueryParams.Where(p => !p.IsEnabled && !string.IsNullOrWhiteSpace(p.Key)).ToList();

        var fragment = string.Empty;
        var hashIndex = url.IndexOf('#');
        if (hashIndex >= 0)
        {
            fragment = url[hashIndex..];
            url = url[..hashIndex];
        }
        var queryIndex = url.IndexOf('?');
        var basePart = queryIndex >= 0 ? url[..queryIndex] : url;
        var gridKeys = enabled.Select(p => p.Key.Trim()).ToHashSet(StringComparer.Ordinal);
        var pairs = (queryIndex >= 0 ? url[(queryIndex + 1)..] : string.Empty)
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(SplitPair)
            .Where(p => !gridKeys.Contains(p.Key))
            .Concat(enabled.Select(p => (Key: p.Key.Trim(), Value: (string?)p.Value)))
            .ToList();

        var query = string.Join('&', pairs.Select(p => p.Value is null ? p.Key : $"{p.Key}={p.Value}"));
        var raw = basePart + (query.Length > 0 ? "?" + query : string.Empty) + fragment;
        if (disabled.Count == 0)
            return JsonValue.Create(raw);

        var result = new JsonObject { ["raw"] = raw };
        var rest = basePart;
        var schemeEnd = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            result["protocol"] = rest[..schemeEnd];
            rest = rest[(schemeEnd + 3)..];
        }
        var slash = rest.IndexOf('/');
        var authority = slash >= 0 ? rest[..slash] : rest;
        var path = slash >= 0 ? rest[(slash + 1)..] : string.Empty;
        var portMatch = PortSuffix().Match(authority);
        if (portMatch.Success)
        {
            result["port"] = portMatch.Groups[1].Value;
            authority = authority[..portMatch.Index];
        }
        // A placeholder host ("{{baseUrl}}") is one part; dots inside it aren't separators.
        result["host"] = new JsonArray(
            (authority.Contains("{{", StringComparison.Ordinal) ? new[] { authority } : authority.Split('.'))
            .Select(part => (JsonNode?)JsonValue.Create(part)).ToArray());
        if (path.Length > 0)
            result["path"] = new JsonArray(path.Split('/').Select(part => (JsonNode?)JsonValue.Create(part)).ToArray());

        var queryArray = new JsonArray();
        foreach (var (key, value) in pairs)
            queryArray.Add(new JsonObject { ["key"] = key, ["value"] = value });
        foreach (var param in disabled)
            queryArray.Add(new JsonObject { ["key"] = param.Key.Trim(), ["value"] = param.Value, ["disabled"] = true });
        result["query"] = queryArray;
        return result;
    }

    [GeneratedRegex(@":(\d+)$")]
    private static partial Regex PortSuffix();

    private static (string Key, string? Value) SplitPair(string segment)
    {
        var equals = segment.IndexOf('=');
        return equals >= 0 ? (segment[..equals], segment[(equals + 1)..]) : (segment, null);
    }

    private static JsonObject? WriteBody(RequestModel request)
    {
        switch (request.BodyMode)
        {
            case BodyMode.Raw or BodyMode.Json or BodyMode.Xml:
                var language = request.BodyMode switch { BodyMode.Json => "json", BodyMode.Xml => "xml", _ => "text" };
                return new JsonObject
                {
                    ["mode"] = "raw",
                    ["raw"] = request.BodyText,
                    ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = language } }
                };

            case BodyMode.FormUrlEncoded:
                var encoded = new JsonArray();
                foreach (var field in request.FormFields.Where(f => f.Kind == FormFieldKind.Text && !string.IsNullOrWhiteSpace(f.Key)))
                {
                    var entry = new JsonObject { ["key"] = field.Key, ["value"] = field.Value, ["type"] = "text" };
                    if (!field.IsEnabled)
                        entry["disabled"] = true;
                    encoded.Add(entry);
                }
                return new JsonObject { ["mode"] = "urlencoded", ["urlencoded"] = encoded };

            case BodyMode.FormData:
                var parts = new JsonArray();
                foreach (var field in request.FormFields.Where(f => !string.IsNullOrWhiteSpace(f.Key)))
                {
                    var entry = new JsonObject { ["key"] = field.Key };
                    if (field.Kind == FormFieldKind.File)
                    {
                        entry["type"] = "file";
                        entry["src"] = field.FilePath;
                    }
                    else
                    {
                        entry["type"] = "text";
                        entry["value"] = field.Value;
                    }
                    if (!string.IsNullOrWhiteSpace(field.ContentType))
                        entry["contentType"] = field.ContentType;
                    if (!field.IsEnabled)
                        entry["disabled"] = true;
                    parts.Add(entry);
                }
                return new JsonObject { ["mode"] = "formdata", ["formdata"] = parts };

            case BodyMode.Binary:
                return new JsonObject { ["mode"] = "file", ["file"] = new JsonObject { ["src"] = request.BinaryFilePath } };

            default:
                return null;
        }
    }

    private static JsonObject? WriteAuth(RequestModel request)
    {
        static JsonObject Param(string key, string value) => new() { ["key"] = key, ["value"] = value, ["type"] = "string" };

        return request.AuthMode switch
        {
            AuthMode.Bearer => new JsonObject
            {
                ["type"] = "bearer",
                ["bearer"] = new JsonArray(Param("token", request.AuthToken))
            },
            AuthMode.Basic => new JsonObject
            {
                ["type"] = "basic",
                ["basic"] = new JsonArray(Param("username", request.AuthUsername), Param("password", request.AuthPassword))
            },
            AuthMode.ApiKey => new JsonObject
            {
                ["type"] = "apikey",
                ["apikey"] = new JsonArray(
                    Param("key", string.IsNullOrWhiteSpace(request.ApiKeyHeader) ? RequestComposer.DefaultApiKeyHeader : request.ApiKeyHeader),
                    Param("value", request.ApiKeyValue),
                    Param("in", "header"))
            },
            _ => null
        };
    }

    private static JsonArray? WriteEvents(string preRequest, string test)
    {
        var events = new JsonArray();
        Add("prerequest", preRequest);
        Add("test", test);
        return events.Count > 0 ? events : null;

        void Add(string listen, string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;
            var lines = new JsonArray(code.ReplaceLineEndings("\n").Split('\n').Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            events.Add(new JsonObject
            {
                ["listen"] = listen,
                ["script"] = new JsonObject { ["type"] = "text/javascript", ["exec"] = lines }
            });
        }
    }

    // ==================== JSON helpers ====================

    private static IEnumerable<JsonObject> Objects(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    // Text of a scalar; numbers and booleans in their JSON spelling (variable values are often numbers).
    private static string? Str(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    }

    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string JoinParts(JsonNode? node, string separator) =>
        node is JsonArray parts
            ? string.Join(separator, parts.Select(p => p is JsonObject o ? Str(o["value"]) : Str(p)))
            : Str(node) ?? string.Empty;
}
