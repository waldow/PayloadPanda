namespace PayloadPanda.Models;

public class RequestTabSession
{
    public int SelectedIndex { get; set; }
    public List<RequestTabDraft> Tabs { get; set; } = [];
}

public class RequestTabDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SavedRequestId { get; set; }
    public string ActiveRequestName { get; set; } = string.Empty;
    public RequestMode RequestMode { get; set; } = RequestMode.Http;
    public int SelectedRequestTabIndex { get; set; }
    public int SelectedResponseTabIndex { get; set; }
    public bool IsDirty { get; set; }
    public RequestModel Request { get; set; } = new();

    /// <summary>Set on load when a stored secret couldn't be decrypted (left empty). Not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SecretsUnreadable { get; set; }
}
