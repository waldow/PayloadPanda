using System.IO;
using System.Net.Http.Headers;
using System.Text;

namespace PayloadPanda.Services;

// Shared by HttpService and RawSocketService: a size-capped body read and charset-aware
// decoding, so both transports turn the same bytes into the same text.
internal static class ResponseText
{
    // Bodies beyond this are cut off (and flagged) rather than freezing the UI.
    public const long MaxBodyBytes = 64L * 1024 * 1024;

    private const int ReadBufferSize = 16 * 1024;

    static ResponseText()
    {
        // Adds legacy code pages (windows-1252, shift_jis, ...) for the charset parameter.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(
        Stream stream, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferSize];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            var room = maxBytes - buffer.Length;
            if (read > room)
            {
                buffer.Write(chunk, 0, (int)room);
                return (buffer.ToArray(), true);
            }
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), false);
    }

    /// <summary>
    /// Decodes a body: a byte-order mark wins, then the Content-Type charset, then UTF-8
    /// (the default for JSON and most APIs). The BOM itself is never part of the text.
    /// </summary>
    public static string Decode(byte[] bytes, string? contentType)
    {
        var encoding = DetectBom(bytes, out var bomLength) ?? CharsetEncoding(contentType) ?? Encoding.UTF8;
        return encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
    }

    private static Encoding? DetectBom(byte[] bytes, out int length)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            length = 3;
            return Encoding.UTF8;
        }
        if (bytes is [0xFF, 0xFE, ..])
        {
            length = 2;
            return Encoding.Unicode;
        }
        if (bytes is [0xFE, 0xFF, ..])
        {
            length = 2;
            return Encoding.BigEndianUnicode;
        }

        length = 0;
        return null;
    }

    private static Encoding? CharsetEncoding(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            !MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.CharSet))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(parsed.CharSet.Trim('"', ' '));
        }
        catch (ArgumentException)
        {
            return null; // unknown charset name: fall back to UTF-8
        }
    }
}
