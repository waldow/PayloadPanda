namespace PayloadPanda.Models;

public class ResponseModel
{
    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = [];
    public string Body { get; set; } = string.Empty;
    public byte[] BodyBytes { get; set; } = [];
    public TimeSpan Duration { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long ResponseSize { get; set; }

    /// <summary>
    /// Low-level connection diagnostics. Populated only by <c>RawSocketService</c>;
    /// always null for responses produced by the standard <c>HttpService</c>.
    /// </summary>
    public ConnectionDiagnostics? Diagnostics { get; set; }
}
