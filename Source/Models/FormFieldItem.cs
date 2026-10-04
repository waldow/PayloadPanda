using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PayloadPanda.Services;

namespace PayloadPanda.Models;

/// <summary>One row of the URL-encoded / form-data editor.</summary>
public partial class FormFieldItem : ObservableObject
{
    /// <summary>
    /// The properties saved with the request. Changes to the others (file size, missing
    /// flag, labels) are display-only and must not mark the request as edited.
    /// </summary>
    public static IReadOnlySet<string> PersistedProperties { get; } = new HashSet<string>
    {
        nameof(Key), nameof(Value), nameof(IsEnabled), nameof(Kind), nameof(FilePath), nameof(ContentType)
    };

    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFile), nameof(ContentTypePlaceholder))]
    private FormFieldKind _kind = FormFieldKind.Text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(FileName), nameof(ExtensionLabel), nameof(DetectedContentType), nameof(ContentTypePlaceholder))]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _contentType = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSizeText))]
    private long? _fileSize;

    [ObservableProperty]
    private bool _fileMissing;

    public bool IsFile => Kind == FormFieldKind.File;
    public bool HasFile => !string.IsNullOrWhiteSpace(FilePath);
    public string FileName => Path.GetFileName(FilePath);
    public string FileSizeText => FileSize is { } size ? ByteSize.Format(size) : string.Empty;
    public string DetectedContentType => MimeTypes.FromFileName(FilePath);
    public string ExtensionLabel => ExtensionBadge(FilePath);

    // Shown in the empty content-type box: what's sent when it's left blank.
    public string ContentTypePlaceholder => IsFile ? $"auto: {DetectedContentType}" : "none";

    public bool IsBlank =>
        string.IsNullOrWhiteSpace(Key) && string.IsNullOrWhiteSpace(Value) && !HasFile && string.IsNullOrWhiteSpace(ContentType);

    partial void OnFilePathChanged(string value) => RefreshFileInfo();

    /// <summary>Re-reads the file's size and existence (cheap; no file contents are read).</summary>
    public void RefreshFileInfo()
    {
        if (!HasFile)
        {
            FileSize = null;
            FileMissing = false;
            return;
        }

        try
        {
            var info = new FileInfo(FilePath);
            FileMissing = !info.Exists;
            FileSize = info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            FileMissing = true;
            FileSize = null;
        }
    }

    public FormFieldData ToData() => new()
    {
        Key = Key,
        Value = Value,
        IsEnabled = IsEnabled,
        Kind = Kind,
        FilePath = FilePath,
        ContentType = ContentType
    };

    public static FormFieldItem FromData(FormFieldData data) => new()
    {
        Key = data.Key,
        Value = data.Value,
        IsEnabled = data.IsEnabled,
        Kind = data.Kind,
        FilePath = data.FilePath,
        ContentType = data.ContentType
    };

    /// <summary>"PNG", "PDF", … for a file badge; "FILE" when there's no usable extension.</summary>
    public static string ExtensionBadge(string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty).TrimStart('.');
        return extension.Length is > 0 and <= 4 ? extension.ToUpperInvariant() : "FILE";
    }
}
