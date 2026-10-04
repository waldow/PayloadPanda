using System.IO;
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
        // Describe: curl reads body files itself, so exporting never touches the file system
        // beyond a cheap existence check for the warning.
        var composed = RequestComposer.Compose(request, includeClientDefaults: false, options: ComposeOptions.Describe);
        var warnings = new List<string>();

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

        var isFormData = composed.HasBody && request.BodyMode == BodyMode.FormData;
        foreach (var (key, value) in composed.Headers)
        {
            // Basic auth reads better as -u user:pass; curl builds the same header from it.
            if (request.AuthMode == AuthMode.Basic && key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            // With -F curl writes multipart/form-data and its own boundary.
            if (isFormData && key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;

            // "Name:" with nothing after it tells curl to remove the header; "Name;" sends it empty.
            var header = value.Length == 0 ? $"{key};" : $"{key}: {value}";
            sb.Append(nl).Append("-H ").Append(Quote(header, style));
        }

        if (request.AuthMode == AuthMode.Basic)
            sb.Append(nl).Append("-u ").Append(Quote($"{request.AuthUsername}:{request.AuthPassword}", style));

        if (isFormData)
        {
            AppendFormParts(sb, nl, composed.FormParts, style, warnings);
        }
        else if (composed.HasBody && request.BodyMode == BodyMode.Binary)
        {
            AppendBinaryBody(sb, nl, composed.BinaryFilePath, style, warnings);
        }
        else if (composed.HasBody && composed.BodyText.Length > 0)
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
                    warnings.Add("cmd.exe can't carry line breaks, so they were replaced with spaces in the body");
                }
            }

            // --data-raw, unlike -d, doesn't treat a leading @ as "read this file".
            sb.Append(nl).Append("--data-raw ").Append(Quote(body, style));
        }

        return new CurlExportResult(sb.ToString(), warnings.Count == 0 ? null : string.Join("; ", warnings));
    }

    // -F and --form-string, one argument per part, in order:
    //  - text:            --form-string 'name=value'   (the value is literal: no @file, <file or ;type=)
    //  - text with type:  -F 'name="value";type=T'    (quoting the value also disables @/<)
    //  - file:            -F 'name=@path;type=T'      (always pass type= so curl doesn't guess differently)
    // Inside curl's "..." only \\ and \" are escapes, so those are the only characters escaped.
    private static void AppendFormParts(StringBuilder sb, string nl, IReadOnlyList<ComposedFormPart> parts,
        CurlExportStyle style, List<string> warnings)
    {
        if (parts.Count == 0)
            warnings.Add("the form has no fields, so curl sends no body");

        foreach (var part in parts)
        {
            if (part.Name.Contains('='))
            {
                warnings.Add($"curl can't send the field \"{part.Name}\" because its name contains '='; it was left out");
                continue;
            }

            string argument;
            if (part.Kind == FormFieldKind.File)
            {
                if (string.IsNullOrWhiteSpace(part.FilePath))
                {
                    warnings.Add($"the field \"{part.Name}\" has no file chosen; it was left out");
                    continue;
                }
                if (!File.Exists(part.FilePath))
                    warnings.Add($"the file for \"{part.Name}\" wasn't found: {part.FilePath}");

                argument = $"-F {Quote($"{part.Name}=@{CurlFormPath(part.FilePath)}{CurlTypeOption(part.ContentType!)}", style)}";
            }
            else
            {
                var value = part.Value;
                if (style == CurlExportStyle.Cmd && value.IndexOfAny(['\r', '\n']) >= 0)
                {
                    value = value.ReplaceLineEndings(" ");
                    warnings.Add($"cmd.exe can't carry line breaks, so they were replaced with spaces in \"{part.Name}\"");
                }

                argument = part.ContentType is null
                    ? $"--form-string {Quote($"{part.Name}={value}", style)}"
                    : $"-F {Quote($"{part.Name}={CurlQuoted(value)}{CurlTypeOption(part.ContentType)}", style)}";
            }

            sb.Append(nl).Append(argument);
        }
    }

    private static void AppendBinaryBody(StringBuilder sb, string nl, string? path, CurlExportStyle style, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            warnings.Add("no body file is chosen, so curl sends no body");
            return;
        }
        if (!File.Exists(path))
            warnings.Add($"the body file wasn't found: {path}");

        // --data-binary @file sends the file byte for byte (-d @file would strip newlines);
        // a bare "-" would mean stdin, so make it a relative path.
        var file = path == "-" ? "./-" : path;
        sb.Append(nl).Append("--data-binary ").Append(Quote($"@{file}", style));
    }

    // A path is passed as is unless it contains a character curl's -F parser treats as
    // syntax (; and , separate options and files, " starts a quoted name) or has
    // surrounding spaces; then it's quoted, escaping \\ and " the way curl expects.
    private static string CurlFormPath(string path) =>
        path.IndexOfAny([';', ',', '"']) >= 0 || path != path.Trim() ? CurlQuoted(path) : path;

    // A type containing ';' (e.g. "application/json; charset=utf-8") can't follow type=,
    // so it's sent as an explicit part header instead.
    private static string CurlTypeOption(string contentType) =>
        contentType.Contains(';') ? $";headers={CurlQuoted($"Content-Type: {contentType}")}" : $";type={contentType}";

    private static string CurlQuoted(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

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
