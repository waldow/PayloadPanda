using PayloadPanda.Services;

namespace PayloadPanda.Models;

public class RequestModel
{
    public HttpMethodType Method { get; set; } = HttpMethodType.GET;
    public string Url { get; set; } = string.Empty;
    public List<HeaderItemData> Headers { get; set; } = [];
    public List<QueryParamData> QueryParams { get; set; } = [];
    public BodyMode BodyMode { get; set; } = BodyMode.None;
    public string BodyText { get; set; } = string.Empty;

    // Fields for the URL-encoded and form-data body modes (one list, so switching
    // between the two keeps them). File rows are only sent as multipart.
    public List<FormFieldData> FormFields { get; set; } = [];

    // The file sent as the whole body in Binary mode.
    public string BinaryFilePath { get; set; } = string.Empty;

    public AuthMode AuthMode { get; set; } = AuthMode.None;
    public string AuthToken { get; set; } = string.Empty;
    public string AuthUsername { get; set; } = string.Empty;
    public string AuthPassword { get; set; } = string.Empty;
    public string ApiKeyHeader { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public bool FollowRedirects { get; set; } = true;

    // CORS troubleshooting
    public bool CorsEnabled { get; set; } = false;
    public string CorsOrigin { get; set; } = string.Empty;
    public string CorsRequestMethod { get; set; } = string.Empty;   // Access-Control-Request-Method (preflight)
    public string CorsRequestHeaders { get; set; } = string.Empty;  // Access-Control-Request-Headers (preflight)
    public bool CorsIncludeCredentials { get; set; } = false;

    // Explicit nulls in hand-edited or AI-generated JSON ({"headers": null})
    // overwrite the non-null defaults during deserialization; call after every
    // load from disk to restore the invariants the rest of the app relies on.
    public void Normalize()
    {
        Url ??= string.Empty;
        Headers ??= [];
        Headers.RemoveAll(h => h is null);
        QueryParams ??= [];
        QueryParams.RemoveAll(p => p is null);
        FormFields ??= [];
        FormFields.RemoveAll(f => f is null);
        BodyText ??= string.Empty;
        BinaryFilePath ??= string.Empty;
        AuthToken ??= string.Empty;
        AuthUsername ??= string.Empty;
        AuthPassword ??= string.Empty;
        ApiKeyHeader ??= string.Empty;
        ApiKeyValue ??= string.Empty;
        CorsOrigin ??= string.Empty;
        CorsRequestMethod ??= string.Empty;
        CorsRequestHeaders ??= string.Empty;

        // A number the enum doesn't define (hand-edited file, newer version) falls back
        // to the default instead of flowing through as an unknown mode.
        if (!Enum.IsDefined(Method)) Method = HttpMethodType.GET;
        if (!Enum.IsDefined(BodyMode)) BodyMode = BodyMode.None;
        if (!Enum.IsDefined(AuthMode)) AuthMode = AuthMode.None;

        foreach (var header in Headers)
        {
            header.Key ??= string.Empty;
            header.Value ??= string.Empty;
        }
        foreach (var param in QueryParams)
        {
            param.Key ??= string.Empty;
            param.Value ??= string.Empty;
        }
        foreach (var field in FormFields)
        {
            field.Key ??= string.Empty;
            field.Value ??= string.Empty;
            field.FilePath ??= string.Empty;
            field.ContentType ??= string.Empty;
            if (!Enum.IsDefined(field.Kind)) field.Kind = FormFieldKind.Text;
        }

        MigrateLegacyUrlEncodedBody();
    }

    // URL-encoded bodies used to be hand-typed text in BodyText. Turn that text into
    // form rows only when re-encoding the rows reproduces it exactly; otherwise keep it
    // as a raw body with the same Content-Type, so the bytes on the wire never change.
    // BodyText itself is left as it was either way.
    private void MigrateLegacyUrlEncodedBody()
    {
        if (BodyMode != BodyMode.FormUrlEncoded || FormFields.Count > 0 || BodyText.Length == 0)
            return;

        if (FormUrlEncoding.TryParseLossless(BodyText, out var pairs))
        {
            FormFields = pairs
                .Select(p => new FormFieldData { Key = p.Key, Value = p.Value })
                .ToList();
            return;
        }

        BodyMode = BodyMode.Raw;
        if (!Headers.Any(h => h.Key.Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            Headers.Add(new HeaderItemData { Key = "Content-Type", Value = "application/x-www-form-urlencoded" });
    }

    // Deep copy. MemberwiseClone covers every string/value-type property; the lists
    // are the only reference-type state and are copied item by item. A new
    // list-valued property must be added here too.
    public RequestModel Clone()
    {
        var clone = (RequestModel)MemberwiseClone();
        clone.Headers = Headers
            .Select(h => new HeaderItemData { Key = h.Key, Value = h.Value, IsEnabled = h.IsEnabled })
            .ToList();
        clone.QueryParams = QueryParams
            .Select(p => new QueryParamData { Key = p.Key, Value = p.Value, IsEnabled = p.IsEnabled })
            .ToList();
        clone.FormFields = FormFields.Select(f => f.Clone()).ToList();
        return clone;
    }
}

public class HeaderItemData
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public class QueryParamData
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public class FormFieldData
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public FormFieldKind Kind { get; set; } = FormFieldKind.Text;

    // Kept even while the row is a Text row, so switching the type back loses nothing.
    public string FilePath { get; set; } = string.Empty;

    // Optional per-part Content-Type; empty means "detect" (files) or "none" (text).
    public string ContentType { get; set; } = string.Empty;

    public FormFieldData Clone() => (FormFieldData)MemberwiseClone();
}
