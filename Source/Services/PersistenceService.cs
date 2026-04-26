using System.IO;
using System.Text.Json;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class PersistenceService
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

    private string? _customHistoryFilePath;

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

    private static string DefaultHistoryFilePath => Path.Combine(AppDataFolder, "history.json");
    private static string SettingsFilePath => Path.Combine(AppDataFolder, "settings.json");

    public string EffectiveHistoryFilePath =>
        !string.IsNullOrWhiteSpace(_customHistoryFilePath) ? _customHistoryFilePath : DefaultHistoryFilePath;

    public void SetHistoryFilePath(string? path)
    {
        _customHistoryFilePath = path;

        // Ensure parent directory exists for custom paths
        if (!string.IsNullOrWhiteSpace(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
    }

    // ==================== Settings ====================

    public async Task SaveSettingsAsync(SettingsModel settings)
    {
        var forDisk = settings.Clone();
        forDisk.OpenAiApiKey = SecretProtector.Protect(forDisk.OpenAiApiKey);
        var json = JsonSerializer.Serialize(forDisk, JsonOptions);
        await File.WriteAllTextAsync(SettingsFilePath, json);
    }

    public async Task<SettingsModel> LoadSettingsAsync()
    {
        if (!File.Exists(SettingsFilePath))
            return new SettingsModel();

        try
        {
            var json = await File.ReadAllTextAsync(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<SettingsModel>(json, ReadOptions) ?? new SettingsModel();
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
        var json = JsonSerializer.Serialize(request, JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<RequestModel?> LoadRequestAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<RequestModel>(json, ReadOptions);
    }

    // ==================== History ====================

    public async Task SaveHistoryAsync(List<HistoryItem> history)
    {
        var json = JsonSerializer.Serialize(history, JsonOptions);
        await File.WriteAllTextAsync(EffectiveHistoryFilePath, json);
    }

    public async Task<List<HistoryItem>> LoadHistoryAsync()
    {
        if (!File.Exists(EffectiveHistoryFilePath))
            return [];

        var json = await File.ReadAllTextAsync(EffectiveHistoryFilePath);
        return JsonSerializer.Deserialize<List<HistoryItem>>(json, ReadOptions) ?? [];
    }

    // ==================== Serialization Helpers ====================

    // Used for history snapshots in %AppData%/PayloadPanda/history.json. The
    // auth fields are DPAPI-encrypted before write and decrypted after read.
    // File export/import goes through SaveRequestAsync/LoadRequestAsync, which
    // stay plaintext so users can share request JSON.
    public string SerializeRequest(RequestModel request)
    {
        var protectedClone = RequestSecrets.ProtectClone(request);
        return JsonSerializer.Serialize(protectedClone, JsonOptions);
    }

    public RequestModel? DeserializeRequest(string json)
    {
        var request = JsonSerializer.Deserialize<RequestModel>(json, ReadOptions);
        if (request is null) return null;

        RequestSecrets.UnprotectInPlace(request);
        return request;
    }
}
