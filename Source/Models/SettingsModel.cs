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

    // AI Import
    public string? OpenAiApiKey { get; set; }
    public string AiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string AiDefaultModel { get; set; } = "gpt-5-nano";
    public int AiTimeoutSeconds { get; set; } = 60;

    public SettingsModel Clone()
    {
        return new SettingsModel
        {
            HistoryFilePath = HistoryFilePath,
            MaxHistoryItems = MaxHistoryItems,
            DefaultTimeoutSeconds = DefaultTimeoutSeconds,
            DefaultFollowRedirects = DefaultFollowRedirects,
            SslCertificateVerification = SslCertificateVerification,
            EditorFontSize = EditorFontSize,
            EditorWordWrap = EditorWordWrap,
            OpenAiApiKey = OpenAiApiKey,
            AiEndpoint = AiEndpoint,
            AiDefaultModel = AiDefaultModel,
            AiTimeoutSeconds = AiTimeoutSeconds
        };
    }
}
