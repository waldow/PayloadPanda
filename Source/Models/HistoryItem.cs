namespace PayloadPanda.Models;

public class HistoryItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public HttpMethodType Method { get; set; }
    public string Url { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public TimeSpan Duration { get; set; }
    public string RequestSnapshot { get; set; } = string.Empty;
    public Guid? SavedRequestId { get; set; }
    public string? SavedRequestName { get; set; }
}
