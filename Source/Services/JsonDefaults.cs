using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayloadPanda.Services;

internal static class JsonDefaults
{
    // On-disk format: camelCase, indented, enums as names ("POST" rather than 1) so
    // exported and hand-edited files survive enum changes. The converter still reads
    // the integer form older versions wrote.
    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // For text shown to the user: keeps non-ASCII and HTML-sensitive characters as
    // they are instead of é / < escapes. Display only — never for HTML output.
    public static readonly JsonSerializerOptions Display = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonWriterOptions s_compactWriter = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Pretty-prints JSON for display; returns the input unchanged when it isn't JSON.</summary>
    public static string TryFormat(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        try
        {
            using var doc = JsonDocument.Parse(input);
            return JsonSerializer.Serialize(doc, Display);
        }
        catch (JsonException)
        {
            return input;
        }
    }

    /// <summary>Re-writes JSON on a single line (string contents untouched); null when it isn't JSON.</summary>
    public static string? TryMinify(string input)
    {
        try
        {
            using var doc = JsonDocument.Parse(input);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, s_compactWriter))
                doc.WriteTo(writer);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
