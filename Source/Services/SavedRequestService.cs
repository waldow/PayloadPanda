using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class SavedRequestService
{
    private static string RequestsFolder
    {
        get
        {
            var folder = Path.Combine(AppPaths.DataFolder, "requests");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    // Written by versions before the multi-tab session (tabs.json); still read once
    // at startup so an old autosave is restored, then cleared.
    private static string AutosaveFilePath => Path.Combine(AppPaths.DataFolder, "autosave.json");

    // ==================== CRUD ====================

    public async Task SaveAsync(SavedRequest request)
    {
        request.ModifiedAt = DateTime.Now;
        var filePath = Path.Combine(RequestsFolder, $"{request.Id}.json");
        var json = JsonSerializer.Serialize(BuildEncryptedCopy(request), JsonDefaults.Write);
        await AtomicFile.WriteAllTextAsync(filePath, json);
    }

    public Task<List<SavedRequest>> LoadAllAsync() => Task.Run(LoadAll);

    // Synchronous reads spread over a few pool threads: with hundreds of small files the
    // per-file latency (antivirus scanning in particular) dominates, and overlapping it
    // keeps startup fast. One file per request, so every read is independent.
    private static List<SavedRequest> LoadAll()
    {
        var results = new ConcurrentBag<SavedRequest>();
        Parallel.ForEach(
            Directory.EnumerateFiles(RequestsFolder, "*.json"),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            file =>
            {
                try
                {
                    var request = JsonSerializer.Deserialize<SavedRequest>(File.ReadAllText(file), JsonDefaults.Read);
                    if (request?.Request != null)
                    {
                        request.Request.Normalize();
                        RequestSecrets.UnprotectInPlace(request.Request);
                        results.Add(request);
                    }
                }
                catch
                {
                    // Skip corrupt files
                }
            });

        return results.OrderByDescending(r => r.ModifiedAt).ToList();
    }

    public void Delete(Guid id)
    {
        var filePath = Path.Combine(RequestsFolder, $"{id}.json");
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    // ==================== Legacy autosave ====================

    public async Task<SavedRequest?> LoadAutosaveAsync()
    {
        if (!File.Exists(AutosaveFilePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(AutosaveFilePath);
            var request = JsonSerializer.Deserialize<SavedRequest>(json, JsonDefaults.Read);
            if (request?.Request is null)
                return null;

            request.Request.Normalize();
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
        try
        {
            if (File.Exists(AutosaveFilePath))
                File.Delete(AutosaveFilePath);
        }
        catch (IOException)
        {
            // A locked autosave file is harmless — it is overwritten on the next save
            // and must never abort the startup tab restore that calls this.
        }
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
