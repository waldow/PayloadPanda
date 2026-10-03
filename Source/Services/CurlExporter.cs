using System.Text;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

public sealed record CurlExportResult(string Command, string? Warning);

/// <summary>
/// Builds a curl command for the request as <see cref="RequestComposer"/> would send it,
/// quoted for the target shell so pasting it reproduces the request byte for byte.
/// </summary>
public static class CurlExporter
{
    // Characters PowerShell accepts as single-quote delimiters; inside a single-quoted
    // string each must be doubled, or a typographic apostrophe ("don’t") ends the string.
    private const string PowerShellSingleQuotes = "'‘’‚‛";

    /// <exception cref="UriFormatException">The URL isn't absolute.</exception>
    /// <exception cref="FormatException">A header is invalid.</exception>
    public static CurlExportResult Generate(RequestModel request, CurlExportStyle style)
    {
        var composed = RequestComposer.Compose(request, includeClientDefaults: false);
        string? warning = null;

        var cmd = style is CurlExportStyle.PowerShell or CurlExportStyle.WindowsPowerShell ? "curl.exe" : "curl";
        var continuation = style switch
        {
            CurlExportStyle.PowerShell or CurlExportStyle.WindowsPowerShell => "`",
            CurlExportStyle.Cmd => "^",
            _ => "\\"
        };
        var nl = $" {continuation}\n  ";

        var sb = new StringBuilder(cmd);

        // -I sends HEAD and stops after the headers; -X HEAD would wait for a body forever.
        if (composed.Method == HttpMethodType.HEAD)
            sb.Append(" -I");
        else if (composed.Method != HttpMethodType.GET)
            sb.Append($" -X {composed.Method}");

        if (request.FollowRedirects)
            sb.Append(" -L");

        sb.Append($" --max-time {Math.Clamp(request.TimeoutSeconds, 1, 300)}");
        sb.Append(' ').Append(Quote(composed.Uri.AbsoluteUri, style));

        foreach (var (key, value) in composed.Headers)
        {
            // Basic auth reads better as -u user:pass; curl builds the same header from it.
            if (request.AuthMode == AuthMode.Basic && key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            // "Name:" with nothing after it tells curl to remove the header; "Name;" sends it empty.
            var header = value.Length == 0 ? $"{key};" : $"{key}: {value}";
            sb.Append(nl).Append("-H ").Append(Quote(header, style));
        }

        if (request.AuthMode == AuthMode.Basic)
            sb.Append(nl).Append("-u ").Append(Quote($"{request.AuthUsername}:{request.AuthPassword}", style));

        if (composed.HasBody && composed.BodyText.Length > 0)
        {
            var body = composed.BodyText;
            if (style == CurlExportStyle.Cmd && body.IndexOfAny(['\r', '\n']) >= 0)
            {
                // cmd.exe ends a command at a line break, even inside quotes.
                if (JsonDefaults.TryMinify(body) is { } minified)
                {
                    body = minified;
                }
                else
                {
                    body = body.ReplaceLineEndings(" ");
                    warning = "cmd.exe can't carry line breaks, so they were replaced with spaces in the body";
                }
            }

            // --data-raw, unlike -d, doesn't treat a leading @ as "read this file".
            sb.Append(nl).Append("--data-raw ").Append(Quote(body, style));
        }

        return new CurlExportResult(sb.ToString(), warning);
    }

    internal static string Quote(string value, CurlExportStyle style) => style switch
    {
        CurlExportStyle.Cmd => QuoteForCRuntime(value, protectPercentFromCmd: true),
        CurlExportStyle.PowerShell => QuoteForPowerShell(value),
        CurlExportStyle.WindowsPowerShell => QuoteForPowerShell(QuoteForCRuntime(value, protectPercentFromCmd: false)),
        _ => $"'{value.Replace("'", "'\\''")}'"
    };

    private static string QuoteForPowerShell(string value)
    {
        var sb = new StringBuilder("'");
        foreach (var c in value)
        {
            sb.Append(c);
            if (PowerShellSingleQuotes.Contains(c))
                sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    // Quotes an argument for the C runtime that splits curl.exe's command line into argv:
    //  - Wrapped in "...", with each embedded quote written "" (the C runtime reads "" inside
    //    quotes as one literal quote) and backslashes before a quote — or before the closing
    //    quote — doubled, since 2n backslashes + quote means n backslashes.
    //  - cmd.exe also reads the line and toggles its own quote tracking at every ", but with
    //    "" escaping it stays in step, so & | < > ^ remain inside quotes and pass through
    //    instead of being executed. Each % is written "%": the quote right after it means
    //    %...% can never spell an environment variable name, so cmd leaves it alone.
    //  - Windows PowerShell 5.1 passes native arguments verbatim unless it thinks they need
    //    quoting: it wraps an argument in "..." when whitespace appears after an even number
    //    of " characters, without escaping anything inside — which is how {"a": 1} arrives
    //    as {a: 1}. In a string quoted this way every character sits after an odd number of
    //    quotes, so 5.1 never re-wraps it and the C runtime gets it exactly as written.
    //    (PowerShell 7.3+ escapes arguments correctly itself, hence its separate style.)
    internal static string QuoteForCRuntime(string value, bool protectPercentFromCmd)
    {
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2).Append("\"\"");
            }
            else if (c == '%' && protectPercentFromCmd)
            {
                sb.Append('\\', backslashes * 2).Append("\"%\"");
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
