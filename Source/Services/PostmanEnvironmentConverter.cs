using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <param name="IsGlobals">The file is a Postman globals export rather than an environment.</param>
public sealed record PostmanEnvironmentImportResult(EnvironmentModel Environment, bool IsGlobals);

/// <summary>Reads and writes Postman environment and globals files (<c>.postman_environment.json</c>).</summary>
public static class PostmanEnvironmentConverter
{
    private static readonly JsonSerializerOptions s_exportOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <exception cref="FormatException">The text isn't JSON or isn't a Postman environment.</exception>
    public static PostmanEnvironmentImportResult Import(string json)
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

        // The Postman API wraps the environment: { "environment": { ... } }.
        if (root?["environment"] is JsonObject wrapped)
            root = wrapped;
        if (root?["values"] is not JsonArray values)
        {
            throw new FormatException(root?["item"] is JsonArray
                ? "This is a Postman collection, not an environment. Import it from the Collections tab."
                : "This doesn't look like a Postman environment (no \"values\").");
        }

        var isGlobals = Text(root["_postman_variable_scope"]) == "globals";
        var environment = new EnvironmentModel
        {
            Name = Text(root["name"]) is { Length: > 0 } name ? name : isGlobals ? "Globals" : "Imported environment"
        };

        foreach (var entry in values.OfType<JsonObject>())
        {
            var key = Text(entry["key"]);
            if (string.IsNullOrEmpty(key))
                continue;

            environment.Variables.Add(new CollectionVariable
            {
                Key = key,
                Value = Text(entry["value"]) ?? string.Empty,
                // Absent means enabled.
                IsEnabled = entry["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var on) || on,
                IsSecret = Text(entry["type"]) == "secret" || PostmanCollectionConverter.LooksSecret(key)
            });
        }

        return new PostmanEnvironmentImportResult(environment, isGlobals);
    }

    /// <summary>The environment as a Postman file. Secret values are written in plain text.</summary>
    public static string Export(EnvironmentModel environment, bool isGlobals = false)
    {
        var values = new JsonArray();
        foreach (var variable in environment.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Key)))
        {
            values.Add(new JsonObject
            {
                ["key"] = variable.Key,
                ["value"] = variable.Value,
                ["type"] = variable.IsSecret ? "secret" : "default",
                ["enabled"] = variable.IsEnabled
            });
        }

        var root = new JsonObject
        {
            ["id"] = environment.Id.ToString(),
            ["name"] = environment.Name,
            ["values"] = values,
            ["_postman_variable_scope"] = isGlobals ? "globals" : "environment"
        };
        return root.ToJsonString(s_exportOptions);
    }

    // Text of a scalar; numbers and booleans in their JSON spelling.
    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    }
}
