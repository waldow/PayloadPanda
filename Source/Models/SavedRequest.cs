namespace PayloadPanda.Models;

public class SavedRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;
    public RequestModel Request { get; set; } = new();

    /// <summary>
    /// Set on load when a stored secret couldn't be decrypted (the file was written by another
    /// Windows user or PC): those fields are empty in <see cref="Request"/>. Not persisted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SecretsUnreadable { get; set; }
}
