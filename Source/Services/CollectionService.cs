using System.IO;
using System.Text.Json;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <summary>
/// One JSON file per collection under <c>collections/</c> in the data folder. Request
/// credentials and variables marked secret are DPAPI-encrypted on disk, as for saved requests.
/// </summary>
public class CollectionService
{
    private static string CollectionsFolder
    {
        get
        {
            var folder = Path.Combine(AppPaths.DataFolder, "collections");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static string FilePath(Guid id) => Path.Combine(CollectionsFolder, $"{id}.json");

    public async Task SaveAsync(CollectionModel collection)
    {
        collection.ModifiedAt = DateTime.Now;
        var json = JsonSerializer.Serialize(BuildEncryptedCopy(collection), JsonDefaults.Write);
        await AtomicFile.WriteAllTextAsync(FilePath(collection.Id), json).ConfigureAwait(false);
    }

    public Task<List<CollectionModel>> LoadAllAsync() => Task.Run(LoadAll);

    private static List<CollectionModel> LoadAll()
    {
        var results = new List<CollectionModel>();
        foreach (var file in Directory.EnumerateFiles(CollectionsFolder, "*.json"))
        {
            try
            {
                var collection = JsonSerializer.Deserialize<CollectionModel>(File.ReadAllText(file), JsonDefaults.Read);
                if (collection is null)
                    continue;

                collection.Normalize();
                collection.SecretsUnreadable = !UnprotectInPlace(collection);
                results.Add(collection);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // One corrupt or locked file must not hide the other collections.
                ErrorLog.Write($"Load collection {Path.GetFileName(file)}", ex);
            }
        }

        return results.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Delete(Guid id)
    {
        var path = FilePath(id);
        if (File.Exists(path))
            File.Delete(path);
    }

    // The caller's collection is live in the UI, so encryption works on a copy.
    internal static CollectionModel BuildEncryptedCopy(CollectionModel collection)
    {
        var copy = collection.Clone();
        foreach (var variable in copy.Variables.Where(v => v.IsSecret))
            variable.Value = SecretProtector.Protect(variable.Value) ?? string.Empty;
        foreach (var item in copy.AllItems().Where(i => i.Request is not null))
            item.Request = RequestSecrets.ProtectClone(item.Request!);
        return copy;
    }

    /// <returns>False when a secret couldn't be decrypted (it is left empty).</returns>
    internal static bool UnprotectInPlace(CollectionModel collection)
    {
        var ok = true;
        // Values without the DPAPI prefix pass through, so every variable can be checked.
        foreach (var variable in collection.Variables)
        {
            if (!SecretProtector.TryUnprotect(variable.Value, out var plaintext))
                ok = false;
            variable.Value = plaintext ?? string.Empty;
        }
        foreach (var item in collection.AllItems().Where(i => i.Request is not null))
        {
            if (!RequestSecrets.UnprotectInPlace(item.Request!))
                ok = false;
        }
        return ok;
    }
}
