namespace PayloadPanda.Models;

/// <summary>What collections, environments and globals all do with their variable lists.</summary>
public static class VariableListExtensions
{
    /// <summary>The enabled variables as a lookup; a later duplicate key wins, as in Postman.</summary>
    public static Dictionary<string, string> EnabledMap(this IEnumerable<CollectionVariable> variables)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in variables.Where(v => v.IsEnabled && !string.IsNullOrWhiteSpace(v.Key)))
            map[variable.Key.Trim()] = variable.Value;
        return map;
    }

    /// <summary>Sets an existing variable (re-enabling it) or adds a new one.</summary>
    /// <returns>The variable that now holds the value, and whether it was just added.</returns>
    public static (CollectionVariable Variable, bool Added) SetValue(this List<CollectionVariable> variables, string key, string value)
    {
        var existing = variables.LastOrDefault(v => v.Key.Trim() == key);
        if (existing is null)
        {
            var added = new CollectionVariable { Key = key, Value = value };
            variables.Add(added);
            return (added, true);
        }

        existing.Value = value;
        existing.IsEnabled = true;
        return (existing, false);
    }

    public static void RemoveKey(this List<CollectionVariable> variables, string key) =>
        variables.RemoveAll(v => v.Key.Trim() == key);

    /// <summary>Drops nulls and null fields left by a hand-edited file.</summary>
    public static List<CollectionVariable> Normalized(this List<CollectionVariable>? variables)
    {
        variables ??= [];
        variables.RemoveAll(v => v is null);
        foreach (var variable in variables)
        {
            variable.Key ??= string.Empty;
            variable.Value ??= string.Empty;
        }
        return variables;
    }
}
