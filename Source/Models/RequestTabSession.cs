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
}
