using System.IO;
using System.Text.Json;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <summary>
/// Environments as one JSON file each under <c>environments/</c>, and the globals as
/// <c>globals.json</c>. Values of variables marked secret are DPAPI-encrypted on disk.
/// </summary>
public class EnvironmentService
{
    private static string EnvironmentsFolder
    {
        get
        {
            var folder = Path.Combine(AppPaths.DataFolder, "environments");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static string GlobalsFilePath => Path.Combine(AppPaths.DataFolder, "globals.json");

    public Task SaveAsync(EnvironmentModel environment) =>
        WriteAsync(environment, Path.Combine(EnvironmentsFolder, $"{environment.Id}.json"));

    public Task SaveGlobalsAsync(EnvironmentModel globals) => WriteAsync(globals, GlobalsFilePath);

    private static async Task WriteAsync(EnvironmentModel environment, string path)
    {
        environment.ModifiedAt = DateTime.Now;
        var json = JsonSerializer.Serialize(BuildEncryptedCopy(environment), JsonDefaults.Write);
        await AtomicFile.WriteAllTextAsync(path, json).ConfigureAwait(false);
    }

    public Task<List<EnvironmentModel>> LoadAllAsync() => Task.Run(() =>
    {
        var results = new List<EnvironmentModel>();
        foreach (var file in Directory.EnumerateFiles(EnvironmentsFolder, "*.json"))
        {
            if (Read(file) is { } environment)
                results.Add(environment);
        }
        return results.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>The stored globals, or an empty set when there are none yet (or the file is unreadable).</summary>
    public Task<EnvironmentModel> LoadGlobalsAsync() => Task.Run(() =>
    {
        var globals = File.Exists(GlobalsFilePath) ? Read(GlobalsFilePath) : null;
        globals ??= new EnvironmentModel();
        globals.Name = "Globals";
        return globals;
    });

    public void Delete(Guid id)
    {
        var path = Path.Combine(EnvironmentsFolder, $"{id}.json");
        if (File.Exists(path))
            File.Delete(path);
    }

    private static EnvironmentModel? Read(string file)
    {
        try
        {
            var environment = JsonSerializer.Deserialize<EnvironmentModel>(File.ReadAllText(file), JsonDefaults.Read);
            if (environment is null)
                return null;

            environment.Normalize();
            environment.SecretsUnreadable = !UnprotectInPlace(environment);
            return environment;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // One corrupt or locked file must not hide the others.
            ErrorLog.Write($"Load environment {Path.GetFileName(file)}", ex);
            return null;
        }
    }

    // The caller's environment is live in the UI, so encryption works on a copy.
    internal static EnvironmentModel BuildEncryptedCopy(EnvironmentModel environment)
    {
        var copy = environment.Clone();
        foreach (var variable in copy.Variables.Where(v => v.IsSecret))
            variable.Value = SecretProtector.Protect(variable.Value) ?? string.Empty;
        return copy;
    }

    /// <returns>False when a secret couldn't be decrypted (it is left empty).</returns>
    internal static bool UnprotectInPlace(EnvironmentModel environment)
    {
        var ok = true;
        foreach (var variable in environment.Variables)
        {
            if (!SecretProtector.TryUnprotect(variable.Value, out var plaintext))
                ok = false;
            variable.Value = plaintext ?? string.Empty;
        }
        return ok;
    }
}
