namespace PayloadPanda.Models;

public class SettingsModel
{
    // Storage
    public string? HistoryFilePath { get; set; }
    public int MaxHistoryItems { get; set; } = 500;

    // HTTP Defaults
    public int DefaultTimeoutSeconds { get; set; } = 30;
    public bool DefaultFollowRedirects { get; set; } = true;
    public bool SslCertificateVerification { get; set; } = true;

    // Editor
    public int EditorFontSize { get; set; } = 13;
    public bool EditorWordWrap { get; set; } = true;

    // Export
    public CurlExportStyle CurlExportStyle { get; set; } = CurlExportStyle.Bash;

    // AI Import
    public string? OpenAiApiKey { get; set; }
    public string AiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string AiDefaultModel { get; set; } = "gpt-5-nano";
    public int AiTimeoutSeconds { get; set; } = 60;

    // A hand-edited settings.json can hold values the Settings window would reject (font
    // size 0, a negative limit, "aiEndpoint": null); bring them back into range on load.
    public void Normalize()
    {
        var defaults = new SettingsModel();
        if (MaxHistoryItems < 0) MaxHistoryItems = defaults.MaxHistoryItems;
        DefaultTimeoutSeconds = Math.Clamp(DefaultTimeoutSeconds, 1, 300);
        EditorFontSize = Math.Clamp(EditorFontSize, 8, 36);
        AiTimeoutSeconds = Math.Clamp(AiTimeoutSeconds, 1, 300);
        if (!Enum.IsDefined(CurlExportStyle)) CurlExportStyle = defaults.CurlExportStyle;
        if (string.IsNullOrWhiteSpace(AiEndpoint)) AiEndpoint = defaults.AiEndpoint;
        if (string.IsNullOrWhiteSpace(AiDefaultModel)) AiDefaultModel = defaults.AiDefaultModel;
    }

    // Every property is a string or value type, so a shallow copy is a full copy and
    // new settings are picked up without touching this method.
    public SettingsModel Clone() => (SettingsModel)MemberwiseClone();
}
