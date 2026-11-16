using System.Text.Json;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class ModelHardeningTests
{
    [Fact]
    public void Leftover_body_text_of_a_current_format_request_is_not_migrated()
    {
        // Typed as JSON, then switched to URL-encoded before any row was added.
        var request = new RequestModel
        {
            FormatVersion = RequestModel.CurrentFormatVersion,
            BodyMode = BodyMode.FormUrlEncoded,
            BodyText = "{\"a\":1}"
        };
        var reloaded = JsonSerializer.Deserialize<RequestModel>(
            JsonSerializer.Serialize(request, JsonDefaults.Write), JsonDefaults.Read)!;

        reloaded.Normalize();

        Assert.Equal(BodyMode.FormUrlEncoded, reloaded.BodyMode);
        Assert.Empty(reloaded.FormFields);
        Assert.Empty(reloaded.Headers);
    }

    [Fact]
    public void A_legacy_file_is_migrated_once_and_stamped_with_the_current_version()
    {
        var legacy = JsonSerializer.Deserialize<RequestModel>(
            "{\"bodyMode\":\"FormUrlEncoded\",\"bodyText\":\"a=1&b=2\"}", JsonDefaults.Read)!;

        legacy.Normalize();

        Assert.Equal(RequestModel.CurrentFormatVersion, legacy.FormatVersion);
        Assert.Equal(["a", "b"], legacy.FormFields.Select(f => f.Key));
    }

    [Fact]
    public void An_oversized_body_is_left_out_of_the_history_snapshot()
    {
        var persistence = new PersistenceService();
        var request = new RequestModel
        {
            Url = "https://api.example.com/",
            BodyMode = BodyMode.Raw,
            BodyText = new string('x', PersistenceService.MaxHistoryBodyChars + 1)
        };

        var snapshot = persistence.SerializeHistorySnapshot(request, out var bodyOmitted);
        var small = persistence.SerializeHistorySnapshot(new RequestModel { BodyText = "hi" }, out var smallOmitted);

        Assert.True(bodyOmitted);
        Assert.True(snapshot.Length < 10_000);
        Assert.Equal(PersistenceService.MaxHistoryBodyChars + 1, request.BodyText.Length); // caller's model untouched
        Assert.False(smallOmitted);
        Assert.Contains("\"hi\"", small);
    }

    [Fact]
    public void Out_of_range_settings_are_brought_back_into_range()
    {
        var settings = JsonSerializer.Deserialize<SettingsModel>(
            "{\"editorFontSize\":0,\"maxHistoryItems\":-5,\"defaultTimeoutSeconds\":9999,\"aiTimeoutSeconds\":0," +
            "\"aiEndpoint\":null,\"aiDefaultModel\":\"\",\"curlExportStyle\":42}", JsonDefaults.Read)!;

        settings.Normalize();

        Assert.Equal(8, settings.EditorFontSize);
        Assert.Equal(500, settings.MaxHistoryItems);
        Assert.Equal(300, settings.DefaultTimeoutSeconds);
        Assert.Equal(1, settings.AiTimeoutSeconds);
        Assert.Equal(AiImportService.DefaultEndpoint, settings.AiEndpoint);
        Assert.Equal("gpt-5-nano", settings.AiDefaultModel);
        Assert.Equal(CurlExportStyle.Bash, settings.CurlExportStyle);
    }

    [Fact]
    public void An_ai_import_that_uploads_local_files_names_them_in_a_warning()
    {
        var upload = new RequestModel
        {
            BinaryFilePath = @"C:\Users\me\.ssh\id_rsa",
            FormFields =
            [
                new() { Key = "doc", Kind = FormFieldKind.File, FilePath = @"C:\docs\a.pdf" },
                new() { Key = "note", Value = "text", FilePath = @"C:\ignored-for-text-rows.txt" }
            ]
        };

        var warning = AiImportService.FileUploadWarning(upload);

        Assert.Contains(@"C:\docs\a.pdf", warning);
        Assert.Contains(@"C:\Users\me\.ssh\id_rsa", warning);
        Assert.DoesNotContain("ignored-for-text-rows", warning);
        Assert.Null(AiImportService.FileUploadWarning(new RequestModel { Url = "https://api.example.com/" }));
    }
}
