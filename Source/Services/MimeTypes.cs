using System.IO;

namespace PayloadPanda.Services;

/// <summary>
/// Content-Type guesses for uploaded files, from the extension. A fixed table (not the
/// Windows registry) so the result is the same on every machine and in tests.
/// </summary>
public static class MimeTypes
{
    public const string Fallback = "application/octet-stream";

    private static readonly Dictionary<string, string> s_byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".avif"] = "image/avif",
        [".heic"] = "image/heic",
        // Documents
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".rtf"] = "application/rtf",
        // Text and data
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "text/javascript",
        [".mjs"] = "text/javascript",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".graphql"] = "application/graphql",
        // Archives
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".tgz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        // Audio and video
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        // Other
        [".wasm"] = "application/wasm",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
    };

    public static string FromFileName(string? path)
    {
        var extension = string.IsNullOrEmpty(path) ? string.Empty : Path.GetExtension(path);
        return s_byExtension.TryGetValue(extension, out var type) ? type : Fallback;
    }
}
