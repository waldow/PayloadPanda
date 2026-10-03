using System.IO;
using System.Text.Json;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class PersistenceService
{
    private string? _customHistoryFilePath;

    // History is saved fire-and-forget after every send as a read-merge-write;
    // serialize those so rapid sends can't interleave.
    private readonly SemaphoreSlim _historyWriteLock = new(1, 1);

    private static string DefaultHistoryFilePath => Path.Combine(AppPaths.DataFolder, "history.json");
    private static string SettingsFilePath => Path.Combine(AppPaths.DataFolder, "settings.json");

    public string EffectiveHistoryFilePath =>
        !string.IsNullOrWhiteSpace(_customHistoryFilePath) ? _customHistoryFilePath : DefaultHistoryFilePath;

    public void SetHistoryFilePath(string? path)
    {
        _customHistoryFilePath = path;

        // Ensure the parent directory exists for custom paths. Best-effort: an unreachable
        // drive must not break settings loading — the history write fails later instead.
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }
    }

    // ==================== Settings ====================

    public async Task SaveSettingsAsync(SettingsModel settings)
    {
        var forDisk = settings.Clone();
        forDisk.OpenAiApiKey = SecretProtector.Protect(forDisk.OpenAiApiKey);
        var json = JsonSerializer.Serialize(forDisk, JsonDefaults.Write);
        await AtomicFile.WriteAllTextAsync(SettingsFilePath, json);
    }

    public async Task<SettingsModel> LoadSettingsAsync()
    {
        if (!File.Exists(SettingsFilePath))
            return new SettingsModel();

        try
        {
            var json = await File.ReadAllTextAsync(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<SettingsModel>(json, JsonDefaults.Read) ?? new SettingsModel();
            settings.OpenAiApiKey = SecretProtector.Unprotect(settings.OpenAiApiKey);
            return settings;
        }
        catch
        {
            return new SettingsModel();
        }
    }

    // ==================== Requests ====================

    public async Task SaveRequestAsync(RequestModel request, string filePath)
    {
        var json = JsonSerializer.Serialize(request, JsonDefaults.Write);
        await AtomicFile.WriteAllTextAsync(filePath, json);
    }

    public async Task<RequestModel?> LoadRequestAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath);
        var request = JsonSerializer.Deserialize<RequestModel>(json, JsonDefaults.Read);
        request?.Normalize();
        return request;
    }

    // ==================== History ====================

    /// <param name="history">The in-memory history, newest first.</param>
    /// <param name="maxItems">Cap applied after merging; 0 means unlimited.</param>
    /// <param name="mergeWithDisk">
    /// Keep entries another app instance (or another machine sharing the file) wrote in the
    /// meantime, instead of overwriting them. False only for "Clear History".
    /// </param>
    public async Task SaveHistoryAsync(IReadOnlyList<HistoryItem> history, int maxItems, bool mergeWithDisk = true)
    {
        await _historyWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = EffectiveHistoryFilePath;
            IReadOnlyList<HistoryItem> toWrite = mergeWithDisk
                ? MergeHistory(history, await ReadHistoryFileAsync(path).ConfigureAwait(false), maxItems)
                : history;
            var json = JsonSerializer.Serialize(toWrite, JsonDefaults.Write);
            await AtomicFile.WriteAllTextAsync(path, json).ConfigureAwait(false);
        }
        finally
        {
            _historyWriteLock.Release();
        }
    }

    public Task<List<HistoryItem>> LoadHistoryAsync() => ReadHistoryFileAsync(EffectiveHistoryFilePath);

    // Union of both lists, newest first, trimmed to maxItems. Entries are matched on
    // their content (instant, method, URL, status) since history items carry no id.
    internal static List<HistoryItem> MergeHistory(
        IEnumerable<HistoryItem> inMemory, IEnumerable<HistoryItem> onDisk, int maxItems)
    {
        var merged = inMemory
            .Concat(onDisk)
            .DistinctBy(i => (i.Timestamp.ToUniversalTime().Ticks, i.Method, i.Url, i.StatusCode))
            .OrderByDescending(i => i.Timestamp.ToUniversalTime());

        return (maxItems > 0 ? merged.Take(maxItems) : merged).ToList();
    }

    private static async Task<List<HistoryItem>> ReadHistoryFileAsync(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<HistoryItem>>(json, JsonDefaults.Read) ?? [];
            items.RemoveAll(i => i is null);
            return items;
        }
        catch
        {
            // A corrupt or unreadable history file must never block startup.
            return [];
        }
    }

    // ==================== Serialization Helpers ====================

    // Used for history snapshots. The sensitive fields are DPAPI-encrypted before write
    // and decrypted after read. File export/import goes through SaveRequestAsync/
    // LoadRequestAsync, which stay plaintext so users can share request JSON.
    public string SerializeRequest(RequestModel request)
    {
        var protectedClone = RequestSecrets.ProtectClone(request);
        return JsonSerializer.Serialize(protectedClone, JsonDefaults.Write);
    }

    /// <param name="secretsReadable">
    /// False when the snapshot holds secrets this Windows user can't decrypt — typically a
    /// history file shared from another machine. Those fields come back empty.
    /// </param>
    public RequestModel? DeserializeRequest(string json, out bool secretsReadable)
    {
        secretsReadable = true;
        try
        {
            var request = JsonSerializer.Deserialize<RequestModel>(json, JsonDefaults.Read);
            if (request is null) return null;

            request.Normalize();
            secretsReadable = RequestSecrets.UnprotectInPlace(request);
            return request;
        }
        catch (JsonException)
        {
            // Malformed history snapshots load as "nothing" instead of crashing.
            return null;
        }
    }
}
