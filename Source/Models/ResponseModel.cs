namespace PayloadPanda.Models;

public class ResponseModel
{
    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;

    /// <summary>
    /// One entry per header line, exactly as received, so repeated fields such as
    /// <c>Set-Cookie</c> stay separate. Use <see cref="GetHeader"/> for a combined value.
    /// </summary>
    public List<KeyValuePair<string, string>> Headers { get; set; } = [];

    public string Body { get; set; } = string.Empty;
    public byte[] BodyBytes { get; set; } = [];
    public TimeSpan Duration { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long ResponseSize { get; set; }

    /// <summary>True when the body exceeded the size cap and only its first part was kept.</summary>
    public bool IsTruncated { get; set; }

    /// <summary>
    /// Low-level connection diagnostics. Populated only by <c>RawSocketService</c>;
    /// always null for responses produced by the standard <c>HttpService</c>.
    /// </summary>
    public ConnectionDiagnostics? Diagnostics { get; set; }

    /// <summary>
    /// All values of the header (case-insensitive) joined with ", " — how HTTP defines a
    /// repeated field — or null when it is absent. Not meaningful for <c>Set-Cookie</c>.
    /// </summary>
    public string? GetHeader(string name) => FindHeader(Headers, name);

    public static string? FindHeader(IEnumerable<KeyValuePair<string, string>> headers, string name)
    {
        string? combined = null;
        foreach (var (key, value) in headers)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                combined = combined is null ? value : $"{combined}, {value}";
        }
        return combined;
    }
}
