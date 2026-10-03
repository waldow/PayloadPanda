using System.IO;
using System.Text.Json;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public class RequestTabSessionService
{
    private static string SessionFilePath => Path.Combine(AppPaths.DataFolder, "tabs.json");

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

        var json = JsonSerializer.Serialize(encrypted, JsonDefaults.Write);
        await AtomicFile.WriteAllTextAsync(SessionFilePath, json).ConfigureAwait(false);
    }

    public async Task<RequestTabSession?> LoadAsync()
    {
        if (!File.Exists(SessionFilePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(SessionFilePath).ConfigureAwait(false);
            var session = JsonSerializer.Deserialize<RequestTabSession>(json, JsonDefaults.Read);
            if (session is null)
                return null;

            session.Tabs ??= [];
            session.Tabs.RemoveAll(tab => tab?.Request is null);
            foreach (var tab in session.Tabs)
            {
                tab.Request.Normalize();
                RequestSecrets.UnprotectInPlace(tab.Request);
            }

            return session;
        }
        catch
        {
            return null;
        }
    }
}
