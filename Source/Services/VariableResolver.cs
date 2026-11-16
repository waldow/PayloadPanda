using System.Globalization;
using System.Text.RegularExpressions;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <summary>
/// Replaces <c>{{name}}</c> placeholders with collection variable values, the way Postman
/// does: names are trimmed, values may themselves contain placeholders, and an unknown
/// name is left exactly as typed (and reported) rather than sent as an empty string.
/// </summary>
public static partial class VariableResolver
{
    // A value that refers to itself would otherwise expand forever.
    private const int MaxDepth = 10;

    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex Placeholder();

    public static bool HasPlaceholders(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains("{{", StringComparison.Ordinal) && Placeholder().IsMatch(text);

    /// <param name="unresolved">Receives the names that had no value.</param>
    public static string Resolve(string? text, IReadOnlyDictionary<string, string> variables, ISet<string>? unresolved = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (!text.Contains("{{", StringComparison.Ordinal))
            return text;

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var replaced = Placeholder().Replace(text, match =>
            {
                var name = match.Groups[1].Value;
                if (variables.TryGetValue(name, out var value))
                    return value;
                return Dynamic(name) ?? match.Value;
            });
            if (replaced == text)
                break;
            text = replaced;
        }

        if (unresolved is not null)
        {
            foreach (Match match in Placeholder().Matches(text))
                unresolved.Add(match.Groups[1].Value);
        }
        return text;
    }

    /// <summary>
    /// A copy of the request with every placeholder replaced, plus the names that had no
    /// value. Scripts are left alone: they read variables through the pm API instead.
    /// </summary>
    public static (RequestModel Request, IReadOnlyList<string> Unresolved) ResolveRequest(
        RequestModel request, IReadOnlyDictionary<string, string> variables)
    {
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        string R(string? text) => Resolve(text, variables, unresolved);

        var resolved = request.Clone();
        resolved.Url = R(resolved.Url);
        foreach (var header in resolved.Headers)
        {
            header.Key = R(header.Key);
            header.Value = R(header.Value);
        }
        foreach (var param in resolved.QueryParams)
        {
            param.Key = R(param.Key);
            param.Value = R(param.Value);
        }
        foreach (var field in resolved.FormFields)
        {
            field.Key = R(field.Key);
            field.Value = R(field.Value);
            field.FilePath = R(field.FilePath);
            field.ContentType = R(field.ContentType);
        }
        resolved.BodyText = R(resolved.BodyText);
        resolved.BinaryFilePath = R(resolved.BinaryFilePath);
        resolved.AuthToken = R(resolved.AuthToken);
        resolved.AuthUsername = R(resolved.AuthUsername);
        resolved.AuthPassword = R(resolved.AuthPassword);
        resolved.ApiKeyHeader = R(resolved.ApiKeyHeader);
        resolved.ApiKeyValue = R(resolved.ApiKeyValue);
        resolved.CorsOrigin = R(resolved.CorsOrigin);
        resolved.CorsRequestHeaders = R(resolved.CorsRequestHeaders);

        return (resolved, unresolved.ToList());
    }

    // The handful of Postman dynamic variables that need no faker library.
    private static string? Dynamic(string name) => name switch
    {
        "$guid" or "$randomUUID" => Guid.NewGuid().ToString(),
        "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        "$isoTimestamp" => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        "$randomInt" => Random.Shared.Next(0, 1001).ToString(CultureInfo.InvariantCulture),
        _ => null
    };
}
