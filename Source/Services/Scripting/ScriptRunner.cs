using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Runtime;

namespace PayloadPanda.Services.Scripting;

/// <summary>The request as a script sees it. Header changes made by a pre-request script come back in the result.</summary>
public sealed record ScriptRequest(string Method, string Url, IReadOnlyList<KeyValuePair<string, string>> Headers, string Body);

public sealed record ScriptResponse(
    int Code, string Status, double ResponseTimeMs, long Size,
    IReadOnlyList<KeyValuePair<string, string>> Headers, string Body);

public sealed record ScriptTestResult(string Name, bool Passed, string? Error, bool Skipped);

public sealed record ScriptConsoleLine(string Level, string Text);

public sealed class ScriptContext
{
    /// <summary>"prerequest" or "test".</summary>
    public required string EventName { get; init; }
    public string RequestName { get; init; } = string.Empty;
    public required ScriptRequest Request { get; init; }

    /// <summary>Null for pre-request scripts.</summary>
    public ScriptResponse? Response { get; init; }

    /// <summary>The variable layers before the scripts run.</summary>
    public VariableScopes Scopes { get; init; } = VariableScopes.Empty;
}

public sealed class ScriptResult
{
    public List<ScriptTestResult> Tests { get; } = [];
    public List<ScriptConsoleLine> Console { get; } = [];

    /// <summary>The variable layers after the scripts ran (set, changed and unset applied to each).</summary>
    public VariableScopes Scopes { get; set; } = VariableScopes.Empty;

    /// <summary>The request headers after the scripts ran.</summary>
    public List<KeyValuePair<string, string>> RequestHeaders { get; set; } = [];

    /// <summary>An uncaught script error, a syntax error or a broken limit; null when every script finished.</summary>
    public string? Error { get; set; }

    public bool Succeeded => Error is null;
}

/// <summary>
/// Runs request scripts written against a subset of Postman's <c>pm</c> API. Scripts get no
/// access to .NET, the file system or the network: they see a JSON snapshot of the request,
/// response and variables, and what they produce (variables, header changes, test results,
/// console output) comes back as JSON. Each script runs in a fresh engine with time, memory
/// and recursion limits, so a hostile or runaway imported script can't hang or exhaust the app.
/// </summary>
public static class ScriptRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // Bodies beyond this aren't handed to scripts in full (the engine's memory is capped).
    private const int MaxBodyChars = 8 * 1024 * 1024;
    private const long MemoryLimitBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Runs the scripts in order (collection, folders, request), each seeing the variables
    /// and headers the previous one left. Stops at the first script that fails.
    /// </summary>
    public static ScriptResult Run(ScriptContext context, IEnumerable<string?> scripts,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var result = new ScriptResult
        {
            Scopes = context.Scopes,
            RequestHeaders = context.Request.Headers.ToList()
        };

        foreach (var script in scripts)
        {
            if (string.IsNullOrWhiteSpace(script))
                continue;

            RunOne(context, script, result, cancellationToken, timeout ?? DefaultTimeout);
            if (!result.Succeeded)
                break;
        }
        return result;
    }

    private static void RunOne(ScriptContext context, string script, ScriptResult result,
        CancellationToken cancellationToken, TimeSpan timeout)
    {
        Engine? engine = null;
        try
        {
            engine = new Engine(options => options
                .TimeoutInterval(timeout)
                .LimitMemory(MemoryLimitBytes)
                .LimitRecursion(256)
                .CancellationToken(cancellationToken));

            engine.SetValue("__ctxJson", BuildContextJson(context, result));
            engine.Execute(PmPrelude.Source);
            engine.Execute(script);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            result.Error = $"The script ran longer than {timeout.TotalSeconds:0.#} seconds and was stopped.";
        }
        catch (JavaScriptException ex)
        {
            result.Error = Describe(ex);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            // Syntax errors and the engine's memory/recursion limits.
            result.Error = ex.Message;
        }
        finally
        {
            // Whatever the script produced before it stopped still counts.
            if (engine is not null)
                Collect(engine, result);
            engine?.Dispose();
        }
    }

    private static string Describe(JavaScriptException ex)
    {
        try
        {
            var line = ex.Location.Start.Line;
            return line > 0 ? $"{ex.Message} (line {line})" : ex.Message;
        }
        catch (Exception)
        {
            return ex.Message;
        }
    }

    private static string BuildContextJson(ScriptContext context, ScriptResult soFar)
    {
        static JsonArray Headers(IEnumerable<KeyValuePair<string, string>> headers) =>
            new(headers.Select(h => (JsonNode?)new JsonObject { ["key"] = h.Key, ["value"] = h.Value }).ToArray());

        static JsonObject Layer(IReadOnlyDictionary<string, string> values)
        {
            var layer = new JsonObject();
            foreach (var (key, value) in values)
                layer[key] = value;
            return layer;
        }

        var scopes = soFar.Scopes;
        var root = new JsonObject
        {
            ["info"] = new JsonObject { ["eventName"] = context.EventName, ["requestName"] = context.RequestName },
            ["environmentName"] = scopes.EnvironmentName,
            ["scopes"] = new JsonObject
            {
                ["globals"] = Layer(scopes.Globals),
                ["collection"] = Layer(scopes.Collection),
                ["environment"] = Layer(scopes.Environment),
                ["locals"] = Layer(scopes.Locals)
            },
            ["request"] = new JsonObject
            {
                ["method"] = context.Request.Method,
                ["url"] = context.Request.Url,
                ["headers"] = Headers(soFar.RequestHeaders),
                ["body"] = context.Request.Body
            }
        };

        if (context.Response is { } response)
        {
            var body = response.Body;
            if (body.Length > MaxBodyChars)
            {
                body = body[..MaxBodyChars];
                soFar.Console.Add(new ScriptConsoleLine("warn",
                    $"The response body is larger than {MaxBodyChars / (1024 * 1024)} MB; scripts see only the first part."));
            }

            root["response"] = new JsonObject
            {
                ["code"] = response.Code,
                ["status"] = response.Status,
                ["responseTime"] = Math.Round(response.ResponseTimeMs),
                ["size"] = response.Size,
                ["headers"] = Headers(response.Headers),
                ["body"] = body
            };
        }

        return root.ToJsonString();
    }

    private static void Collect(Engine engine, ScriptResult result)
    {
        try
        {
            // The limits that stopped the script would stop this call too.
            engine.Constraints.Reset();
            var json = engine.Evaluate("typeof __collect === 'function' ? __collect() : null");
            if (!json.IsString())
                return;

            using var doc = JsonDocument.Parse(json.AsString());
            var root = doc.RootElement;

            foreach (var test in root.GetProperty("tests").EnumerateArray())
            {
                result.Tests.Add(new ScriptTestResult(
                    Text(test, "name"),
                    test.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True,
                    test.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null,
                    test.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True));
            }

            foreach (var line in root.GetProperty("console").EnumerateArray())
                result.Console.Add(new ScriptConsoleLine(Text(line, "level"), Text(line, "text")));

            var scopes = root.GetProperty("scopes");
            result.Scopes = result.Scopes with
            {
                Globals = Layer(scopes, "globals"),
                Collection = Layer(scopes, "collection"),
                Environment = Layer(scopes, "environment"),
                Locals = Layer(scopes, "locals")
            };

            result.RequestHeaders = root.GetProperty("requestHeaders").EnumerateArray()
                .Select(h => new KeyValuePair<string, string>(Text(h, "key"), Text(h, "value")))
                .ToList();
        }
        catch (Exception ex)
        {
            // The script broke the collector itself (e.g. overwrote it): keep the earlier state.
            result.Error ??= $"The script's results couldn't be read: {ex.Message}";
        }
    }

    private static Dictionary<string, string> Layer(JsonElement scopes, string name)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in scopes.GetProperty(name).EnumerateObject())
            values[variable.Name] = variable.Value.ValueKind == JsonValueKind.String ? variable.Value.GetString()! : variable.Value.GetRawText();
        return values;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
            : string.Empty;
}
