using System.IO;
using System.Text.Json;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class RequestTabSessionService
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

    private static string SessionFilePath => Path.Combine(AppDataFolder, "tabs.json");

    public async Task SaveAsync(RequestTabSession session)
    {
        var encrypted = new RequestTabSession
        {
            SelectedIndex = session.SelectedIndex,
            Tabs = session.Tabs.Select(tab => new RequestTabDraft
            {
                Id = tab.Id,
                SavedRequestId = tab.SavedRequestId,
                ActiveRequestName = tab.ActiveRequestName,
                RequestMode = tab.RequestMode,
                SelectedRequestTabIndex = tab.SelectedRequestTabIndex,
                SelectedResponseTabIndex = tab.SelectedResponseTabIndex,
                IsDirty = tab.IsDirty,
                Request = RequestSecrets.ProtectClone(tab.Request)
            }).ToList()
        };

        var json = JsonSerializer.Serialize(encrypted, JsonOptions);
        await File.WriteAllTextAsync(SessionFilePath, json).ConfigureAwait(false);
    }

    public async Task<RequestTabSession?> LoadAsync()
    {
        if (!File.Exists(SessionFilePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(SessionFilePath).ConfigureAwait(false);
            var session = JsonSerializer.Deserialize<RequestTabSession>(json, ReadOptions);
            if (session is null)
                return null;

            foreach (var tab in session.Tabs)
                RequestSecrets.UnprotectInPlace(tab.Request);

            return session;
        }
        catch
        {
            return null;
        }
    }
}
