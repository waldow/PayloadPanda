using System.IO;
using System.Text.Json;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class SavedRequestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string AppDataFolder
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PayloadPanda");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static string RequestsFolder
    {
        get
        {
            var folder = Path.Combine(AppDataFolder, "requests");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static string AutosaveFilePath => Path.Combine(AppDataFolder, "autosave.json");

    // ==================== CRUD ====================

    public async Task SaveAsync(SavedRequest request)
    {
        request.ModifiedAt = DateTime.Now;
        var filePath = Path.Combine(RequestsFolder, $"{request.Id}.json");
        var json = JsonSerializer.Serialize(BuildEncryptedCopy(request), JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<List<SavedRequest>> LoadAllAsync()
    {
        var folder = RequestsFolder;
        if (!Directory.Exists(folder))
            return [];

        var results = new List<SavedRequest>();
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var request = JsonSerializer.Deserialize<SavedRequest>(json, ReadOptions);
                if (request != null)
                {
                    RequestSecrets.UnprotectInPlace(request.Request);
                    results.Add(request);
                }
            }
            catch
            {
                // Skip corrupt files
            }
        }

        return results.OrderByDescending(r => r.ModifiedAt).ToList();
    }

    public void Delete(Guid id)
    {
        var filePath = Path.Combine(RequestsFolder, $"{id}.json");
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    // ==================== Autosave ====================

    public async Task SaveAutosaveAsync(SavedRequest request)
    {
        var json = JsonSerializer.Serialize(BuildEncryptedCopy(request), JsonOptions);
        await File.WriteAllTextAsync(AutosaveFilePath, json);
    }

    public async Task<SavedRequest?> LoadAutosaveAsync()
    {
        if (!File.Exists(AutosaveFilePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(AutosaveFilePath);
            var request = JsonSerializer.Deserialize<SavedRequest>(json, ReadOptions);
            if (request != null)
                RequestSecrets.UnprotectInPlace(request.Request);
            return request;
        }
        catch
        {
            return null;
        }
    }

    public void ClearAutosave()
    {
        if (File.Exists(AutosaveFilePath))
            File.Delete(AutosaveFilePath);
    }

    // Returns a shallow-cloned SavedRequest whose inner Request has DPAPI-protected
    // auth fields. Cloning is required because callers hold the live SavedRequest
    // instance bound to the UI — encrypting in place would corrupt the in-memory
    // model.
    private static SavedRequest BuildEncryptedCopy(SavedRequest request) => new()
    {
        Id = request.Id,
        Name = request.Name,
        CreatedAt = request.CreatedAt,
        ModifiedAt = request.ModifiedAt,
        Request = RequestSecrets.ProtectClone(request.Request)
    };
}
