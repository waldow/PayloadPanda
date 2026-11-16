using System.Text.Json.Serialization;

namespace PayloadPanda.Models;

/// <summary>
/// A named set of variables (stg, prod, local…). One environment is active at a time and
/// applies to every request; its values win over a collection's. The globals — always on,
/// underneath everything — are stored as one of these too.
/// </summary>
public class EnvironmentModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;
    public List<CollectionVariable> Variables { get; set; } = [];

    /// <summary>Set on load when a stored secret couldn't be decrypted (left empty). Not persisted.</summary>
    [JsonIgnore]
    public bool SecretsUnreadable { get; set; }

    public void Normalize()
    {
        Name ??= string.Empty;
        Variables = Variables.Normalized();
    }

    public Dictionary<string, string> EnabledVariables() => Variables.EnabledMap();

    public EnvironmentModel Clone()
    {
        var clone = (EnvironmentModel)MemberwiseClone();
        clone.Variables = Variables.Select(v => v.Clone()).ToList();
        return clone;
    }
}
