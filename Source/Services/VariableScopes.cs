namespace PayloadPanda.Services;

/// <summary>
/// The variable layers in effect for one send. Precedence, as in Postman, highest first:
/// local (set by a script with <c>pm.variables.set</c>, gone after the send), the active
/// environment, the request's collection, globals.
/// </summary>
public sealed record VariableScopes
{
    private static readonly IReadOnlyDictionary<string, string> s_none = new Dictionary<string, string>();

    public static VariableScopes Empty { get; } = new();

    public IReadOnlyDictionary<string, string> Globals { get; init; } = s_none;
    public IReadOnlyDictionary<string, string> Collection { get; init; } = s_none;
    public IReadOnlyDictionary<string, string> Environment { get; init; } = s_none;
    public IReadOnlyDictionary<string, string> Locals { get; init; } = s_none;

    /// <summary>The active environment's name; null when none is selected.</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>One lookup with every layer applied, for <see cref="VariableResolver"/>.</summary>
    public Dictionary<string, string> Merged()
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in new[] { Globals, Collection, Environment, Locals })
        {
            foreach (var (key, value) in layer)
                merged[key] = value;
        }
        return merged;
    }
}
