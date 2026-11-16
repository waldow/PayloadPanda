using System.Text.Json.Serialization;

namespace PayloadPanda.Models;

/// <summary>
/// A named tree of folders and requests with shared variables and scripts. Stored in
/// PayloadPanda's own format; Postman Collection v2.1 is an import/export format
/// (see <c>PostmanCollectionConverter</c>).
/// </summary>
public class CollectionModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    /// <summary>Substituted for <c>{{key}}</c> in every request of the collection.</summary>
    public List<CollectionVariable> Variables { get; set; } = [];

    public List<CollectionItem> Items { get; set; } = [];

    // Run before/after every request in the collection, ahead of folder and request scripts.
    public string PreRequestScript { get; set; } = string.Empty;
    public string TestScript { get; set; } = string.Empty;

    /// <summary>Set on load when a stored secret couldn't be decrypted (left empty). Not persisted.</summary>
    [JsonIgnore]
    public bool SecretsUnreadable { get; set; }

    // Same purpose as RequestModel.Normalize: explicit nulls in a hand-edited file must
    // not break the invariants the rest of the app relies on.
    public void Normalize()
    {
        Name ??= string.Empty;
        Description ??= string.Empty;
        PreRequestScript ??= string.Empty;
        TestScript ??= string.Empty;
        Variables = Variables.Normalized();
        Items ??= [];
        NormalizeItems(Items);
    }

    private static void NormalizeItems(List<CollectionItem> items)
    {
        items.RemoveAll(i => i is null);
        foreach (var item in items)
        {
            if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
            item.Name ??= string.Empty;
            item.Description ??= string.Empty;
            item.PreRequestScript ??= string.Empty;
            item.TestScript ??= string.Empty;
            item.Items ??= [];
            item.Request?.Normalize();
            NormalizeItems(item.Items);
        }
    }

    public Dictionary<string, string> EnabledVariables() => Variables.EnabledMap();

    /// <summary>Sets an existing variable (re-enabling it) or adds a new one.</summary>
    public void SetVariable(string key, string value) => Variables.SetValue(key, value);

    /// <summary>Every item in the tree, depth first.</summary>
    public IEnumerable<CollectionItem> AllItems() => Flatten(Items);

    private static IEnumerable<CollectionItem> Flatten(IEnumerable<CollectionItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Items))
                yield return child;
        }
    }

    public CollectionItem? FindItem(Guid id) => AllItems().FirstOrDefault(i => i.Id == id);

    /// <summary>The folders leading to the item, outermost first, then the item itself; empty when it isn't here.</summary>
    public List<CollectionItem> FindPath(Guid id)
    {
        var path = new List<CollectionItem>();
        return Search(Items, id, path) ? path : [];
    }

    private static bool Search(List<CollectionItem> items, Guid id, List<CollectionItem> path)
    {
        foreach (var item in items)
        {
            path.Add(item);
            if (item.Id == id || Search(item.Items, id, path))
                return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    /// <summary>The list that directly holds the item (the collection's or a folder's), or null.</summary>
    public List<CollectionItem>? FindContainer(Guid id) => FindContainer(Items, id);

    private static List<CollectionItem>? FindContainer(List<CollectionItem> items, Guid id)
    {
        if (items.Any(i => i.Id == id))
            return items;
        foreach (var item in items)
        {
            if (FindContainer(item.Items, id) is { } found)
                return found;
        }
        return null;
    }

    public CollectionModel Clone()
    {
        var clone = (CollectionModel)MemberwiseClone();
        clone.Variables = Variables.Select(v => v.Clone()).ToList();
        clone.Items = Items.Select(i => i.Clone()).ToList();
        return clone;
    }
}

public class CollectionVariable
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    /// <summary>Encrypted at rest (DPAPI). Exported files stay plaintext, like exported requests.</summary>
    public bool IsSecret { get; set; }

    public CollectionVariable Clone() => (CollectionVariable)MemberwiseClone();
}

/// <summary>A folder (<see cref="Request"/> is null) or a request in a collection.</summary>
public class CollectionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>The request, scripts included; null for a folder.</summary>
    public RequestModel? Request { get; set; }

    /// <summary>A folder's children; empty for a request.</summary>
    public List<CollectionItem> Items { get; set; } = [];

    // A folder's scripts run for every request below it. A request's own scripts live on
    // its RequestModel.
    public string PreRequestScript { get; set; } = string.Empty;
    public string TestScript { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsFolder => Request is null;

    /// <summary>Deep copy that keeps the ids.</summary>
    public CollectionItem Clone()
    {
        var clone = (CollectionItem)MemberwiseClone();
        clone.Request = Request?.Clone();
        clone.Items = Items.Select(i => i.Clone()).ToList();
        return clone;
    }

    /// <summary>Deep copy with fresh ids throughout (duplicating an item).</summary>
    public CollectionItem CloneWithNewIds()
    {
        var clone = Clone();
        Reassign(clone);
        return clone;

        static void Reassign(CollectionItem item)
        {
            item.Id = Guid.NewGuid();
            foreach (var child in item.Items)
                Reassign(child);
        }
    }
}
