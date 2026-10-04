using System.Text;
using System.Text.Json;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.Tests;

public class FormUrlEncodingTests
{
    [Fact]
    public void Serializes_like_a_browser()
    {
        Assert.Equal("a+b=x%26y%3Dz", FormUrlEncoding.Serialize([new("a b", "x&y=z")]));
        Assert.Equal("*-._%7E%C3%A9%2B", FormUrlEncoding.Encode("*-._~é+"));
        Assert.Equal("k=&tag=a&tag=b", FormUrlEncoding.Serialize([new("k", ""), new("tag", "a"), new("tag", "b")]));
    }

    [Fact]
    public void Parses_leniently()
    {
        var pairs = FormUrlEncoding.Parse("a=hello+world&b=%20x&&c=%zz&d&e=%F0%9F%90%BC&f=🐼");

        Assert.Equal(
            [new("a", "hello world"), new("b", " x"), new("c", "%zz"), new("d", ""), new("e", "🐼"), new("f", "🐼")],
            pairs);
    }

    [Theory]
    [InlineData("name=Ada+Lovelace&tag=a&tag=b", true)]
    [InlineData("a=hello%20world", false)]  // would come back as hello+world
    [InlineData("name=John Doe", false)]     // a literal space
    [InlineData("a=1\n", false)]             // a trailing newline
    public void Lossless_parse_only_when_reencoding_matches(string body, bool lossless)
    {
        Assert.Equal(lossless, FormUrlEncoding.TryParseLossless(body, out var pairs));
        Assert.Equal(lossless, pairs.Count > 0);
    }
}

public class RequestModelFormTests
{
    [Fact]
    public void Legacy_urlencoded_text_becomes_rows_when_lossless()
    {
        var request = new RequestModel { BodyMode = BodyMode.FormUrlEncoded, BodyText = "name=Ada+Lovelace&tag=a&tag=b" };

        request.Normalize();

        Assert.Equal(BodyMode.FormUrlEncoded, request.BodyMode);
        Assert.Equal(["name=Ada Lovelace", "tag=a", "tag=b"], request.FormFields.Select(f => $"{f.Key}={f.Value}"));
        Assert.Equal("name=Ada+Lovelace&tag=a&tag=b", request.BodyText);
        Assert.Equal("name=Ada+Lovelace&tag=a&tag=b", RequestComposer.Compose(Post(request), false).BodyText);
    }

    [Fact]
    public void Legacy_urlencoded_text_that_would_change_stays_raw_with_the_same_content_type()
    {
        var request = new RequestModel { BodyMode = BodyMode.FormUrlEncoded, BodyText = "a=hello%20world" };

        request.Normalize();

        Assert.Equal(BodyMode.Raw, request.BodyMode);
        Assert.Empty(request.FormFields);
        var composed = RequestComposer.Compose(Post(request), false);
        Assert.Equal("a=hello%20world", composed.BodyText);
        Assert.Equal("application/x-www-form-urlencoded", composed.GetHeader("Content-Type"));
    }

    [Fact]
    public void Migration_keeps_an_existing_content_type_row_and_skips_requests_that_have_fields()
    {
        var lossy = new RequestModel
        {
            BodyMode = BodyMode.FormUrlEncoded,
            BodyText = "a b",
            Headers = [new() { Key = "content-type", Value = "application/x-www-form-urlencoded; charset=utf-8" }]
        };
        lossy.Normalize();
        Assert.Single(lossy.Headers);

        var withFields = new RequestModel
        {
            BodyMode = BodyMode.FormUrlEncoded,
            BodyText = "old=1",
            FormFields = [new() { Key = "new", Value = "2" }]
        };
        withFields.Normalize();
        Assert.Equal("new", Assert.Single(withFields.FormFields).Key);
    }

    [Fact]
    public void Undefined_enum_numbers_fall_back_to_defaults()
    {
        var request = JsonSerializer.Deserialize<RequestModel>(
            "{\"bodyMode\":9,\"method\":42,\"formFields\":[{\"key\":\"k\",\"kind\":7},null]}", JsonDefaults.Read)!;

        request.Normalize();

        Assert.Equal(BodyMode.None, request.BodyMode);
        Assert.Equal(HttpMethodType.GET, request.Method);
        Assert.Equal(FormFieldKind.Text, Assert.Single(request.FormFields).Kind);
    }

    [Fact]
    public void Clone_and_json_round_trip_keep_form_fields()
    {
        var request = new RequestModel
        {
            BodyMode = BodyMode.FormData,
            FormFields = [new() { Key = "avatar", Kind = FormFieldKind.File, FilePath = @"C:\p.png", ContentType = "image/png" }]
        };

        var clone = request.Clone();
        clone.FormFields[0].Key = "changed";
        Assert.Equal("avatar", request.FormFields[0].Key);

        var json = JsonSerializer.Serialize(request, JsonDefaults.Write);
        Assert.Contains("\"bodyMode\": \"FormData\"", json);
        Assert.Contains("\"kind\": \"File\"", json);
        var back = JsonSerializer.Deserialize<RequestModel>(json, JsonDefaults.Read)!;
        Assert.Equal(@"C:\p.png", back.FormFields[0].FilePath);
    }

    private static RequestModel Post(RequestModel request)
    {
        request.Method = HttpMethodType.POST;
        request.Url = "https://api.example.com/";
        return request;
    }
}

/// <summary>Creates temp files for a test and deletes them afterwards.</summary>
public sealed class TempFiles : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pp-tests-" + Guid.NewGuid().ToString("N"));

    public TempFiles() => Directory.CreateDirectory(_folder);

    public string Create(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string PathFor(string name) => Path.Combine(_folder, name);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}

public class MultipartComposerTests : IDisposable
{
    private readonly TempFiles _files = new();
    private static readonly ComposeOptions FixedBoundary = new() { Boundary = "B" };

    public void Dispose() => _files.Dispose();

    private static RequestModel Form(params FormFieldData[] fields) => new()
    {
        Method = HttpMethodType.POST,
        Url = "https://api.example.com/upload",
        BodyMode = BodyMode.FormData,
        FormFields = fields.ToList()
    };

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Writes_browser_style_multipart()
    {
        var png = _files.Create("panda.png", [1, 2, 3]);
        var request = Form(
            new() { Key = "name", Value = "Ada" },
            new() { Key = "avatar", Kind = FormFieldKind.File, FilePath = png },
            new() { Key = "off", Value = "x", IsEnabled = false },
            new() { Key = " ", Value = "blank key" });

        var composed = RequestComposer.Compose(request, false, options: FixedBoundary);

        Assert.Equal("multipart/form-data; boundary=B", composed.GetHeader("Content-Type"));
        var expected =
            "--B\r\nContent-Disposition: form-data; name=\"name\"\r\n\r\nAda\r\n" +
            "--B\r\nContent-Disposition: form-data; name=\"avatar\"; filename=\"panda.png\"\r\nContent-Type: image/png\r\n\r\n\u0001\u0002\u0003\r\n" +
            "--B--\r\n";
        var body = composed.Body.ToArray();
        Assert.Equal(expected, Latin1(body));
        Assert.Equal(body.Length, composed.Body.Length);
    }

    [Fact]
    public void Escapes_names_like_browsers_and_sends_only_the_file_name()
    {
        var file = _files.Create("notes é.txt", [65]);
        var request = Form(
            new() { Key = "a\"b\r\nc\\d", Value = "v" },
            new() { Key = "f", Kind = FormFieldKind.File, FilePath = file });

        var text = Encoding.UTF8.GetString(RequestComposer.Compose(request, false, options: FixedBoundary).Body.ToArray());

        Assert.Contains("name=\"a%22b%0D%0Ac\\d\"", text);
        Assert.Contains("filename=\"notes é.txt\"", text);   // non-ASCII as raw UTF-8
        Assert.DoesNotContain(Path.GetDirectoryName(file)!, text);

        // Windows can't create a file with " in its name, so check that escaping without I/O.
        var described = RequestComposer.Compose(
            Form(new FormFieldData { Key = "f", Kind = FormFieldKind.File, FilePath = "/data/we\"ird.txt" }),
            false, options: ComposeOptions.Describe);
        Assert.Contains("filename=\"we%22ird.txt\"", described.BodyPreview);
    }

    [Fact]
    public void Text_parts_get_a_content_type_only_when_the_row_sets_one()
    {
        var request = Form(new FormFieldData { Key = "meta", Value = "{\"a\":1}", ContentType = "application/json" });
        var text = Encoding.UTF8.GetString(RequestComposer.Compose(request, false, options: FixedBoundary).Body.ToArray());
        Assert.Contains("name=\"meta\"\r\nContent-Type: application/json\r\n\r\n{\"a\":1}\r\n", text);

        request.FormFields[0].ContentType = "text/plain\r\nX-Injected: 1";
        Assert.Throws<FormatException>(() => RequestComposer.Compose(request, false, options: FixedBoundary));
    }

    [Fact]
    public void Empty_form_is_just_the_closing_boundary()
    {
        Assert.Equal("--B--\r\n", Latin1(RequestComposer.Compose(Form(), false, options: FixedBoundary).Body.ToArray()));
    }

    [Fact]
    public void File_problems_are_reported_with_the_field_name()
    {
        var noPath = Assert.Throws<BodyFileException>(() =>
            RequestComposer.Compose(Form(new FormFieldData { Key = "doc", Kind = FormFieldKind.File }), false));
        Assert.Contains("\"doc\"", noPath.Message);

        var missingPath = _files.PathFor("gone.pdf");
        var missing = Assert.Throws<BodyFileException>(() =>
            RequestComposer.Compose(Form(new FormFieldData { Key = "doc", Kind = FormFieldKind.File, FilePath = missingPath }), false));
        Assert.Contains(missingPath, missing.Message);

        var big = _files.Create("big.bin", new byte[10]);
        Assert.Throws<BodyFileException>(() => RequestComposer.Compose(
            Form(new FormFieldData { Key = "f", Kind = FormFieldKind.File, FilePath = big }), false,
            options: new ComposeOptions { MaxBodyBytes = 5 }));
    }

    [Fact]
    public void Describe_never_touches_the_file_system()
    {
        var request = Form(new FormFieldData { Key = "doc", Kind = FormFieldKind.File, FilePath = _files.PathFor("gone.pdf") });

        var composed = RequestComposer.Compose(request, false, options: ComposeOptions.Describe);

        Assert.Equal(0, composed.Body.Length);
        var part = Assert.Single(composed.FormParts);
        Assert.Equal(("doc", FormFieldKind.File, "application/pdf"), (part.Name, part.Kind, part.ContentType));
        Assert.Contains("<file: gone.pdf, application/pdf>", composed.BodyPreview);
    }

    [Fact]
    public void Form_data_replaces_a_content_type_row_and_get_skips_the_body()
    {
        var request = Form(new FormFieldData { Key = "a", Value = "1" });
        request.Headers = [new() { Key = "Content-Type", Value = "multipart/form-data" }];

        var composed = RequestComposer.Compose(request, false, options: FixedBoundary);
        Assert.Equal("multipart/form-data; boundary=B", Assert.Single(composed.Headers, h => h.Key == "Content-Type").Value);

        request.Method = HttpMethodType.GET;
        request.FormFields.Add(new FormFieldData { Key = "f", Kind = FormFieldKind.File, FilePath = _files.PathFor("missing") });
        var get = RequestComposer.Compose(request, false);
        Assert.False(get.HasBody);
        Assert.Equal(0, get.Body.Length);
    }

    [Fact]
    public void Preview_shows_files_as_placeholders()
    {
        var png = _files.Create("panda.png", Encoding.ASCII.GetBytes("SECRET-BYTES"));
        var composed = RequestComposer.Compose(Form(new FormFieldData { Key = "f", Kind = FormFieldKind.File, FilePath = png }), false);

        Assert.Contains("<file: panda.png, 12 B, image/png>", composed.BodyPreview);
        Assert.DoesNotContain("SECRET-BYTES", composed.BodyPreview);
    }

    [Fact]
    public async Task Sending_fails_clearly_if_the_file_changed_after_composing()
    {
        var file = _files.Create("grow.txt", [1, 2, 3]);
        var composed = RequestComposer.Compose(Form(new FormFieldData { Key = "f", Kind = FormFieldKind.File, FilePath = file }), false);
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        var ex = await Assert.ThrowsAsync<IOException>(() => composed.Body.WriteToAsync(Stream.Null, CancellationToken.None));
        Assert.Contains("grow.txt", ex.Message);
    }

    [Fact]
    public void Url_encoded_uses_text_rows_only()
    {
        var request = Form(
            new() { Key = "name", Value = "Ada Lovelace" },
            new() { Key = "photo", Kind = FormFieldKind.File, FilePath = "x.png" },
            new() { Key = "q", Value = "a&b" });
        request.BodyMode = BodyMode.FormUrlEncoded;

        var composed = RequestComposer.Compose(request, false);

        Assert.Equal("name=Ada+Lovelace&q=a%26b", composed.BodyText);
        Assert.Equal("name=Ada+Lovelace&q=a%26b"u8.ToArray(), composed.Body.ToArray());
        Assert.Equal("application/x-www-form-urlencoded", composed.GetHeader("Content-Type"));
    }

    [Fact]
    public void Binary_sends_the_file_with_a_guessed_or_explicit_type()
    {
        var pdf = _files.Create("report.pdf", [37, 80, 68, 70]);
        var request = new RequestModel
        {
            Method = HttpMethodType.PUT,
            Url = "https://bucket.example.com/report.pdf",
            BodyMode = BodyMode.Binary,
            BinaryFilePath = pdf
        };

        var composed = RequestComposer.Compose(request, false);
        Assert.Equal(new byte[] { 37, 80, 68, 70 }, composed.Body.ToArray());
        Assert.Equal("application/pdf", composed.GetHeader("Content-Type"));
        Assert.Equal(pdf, composed.BinaryFilePath);

        request.Headers = [new() { Key = "Content-Type", Value = "application/octet-stream" }];
        Assert.Equal("application/octet-stream", RequestComposer.Compose(request, false).GetHeader("Content-Type"));

        request.BinaryFilePath = "";
        Assert.Throws<BodyFileException>(() => RequestComposer.Compose(request, false));
    }
}

public class FormCurlExportTests : IDisposable
{
    private readonly TempFiles _files = new();

    public void Dispose() => _files.Dispose();

    private static RequestModel Form(params FormFieldData[] fields) => new()
    {
        Method = HttpMethodType.POST,
        Url = "https://api.example.com/upload",
        BodyMode = BodyMode.FormData,
        FormFields = fields.ToList(),
        FollowRedirects = false
    };

    private static CurlExportResult Export(RequestModel request, CurlExportStyle style = CurlExportStyle.Bash) =>
        CurlExporter.Generate(request, style);

    [Fact]
    public void Text_fields_use_form_string_and_files_use_dash_F_with_a_type()
    {
        var png = _files.Create("panda.png", [1]);
        var result = Export(Form(
            new() { Key = "note", Value = "@not-a-file;type=x/y" },
            new() { Key = "avatar", Kind = FormFieldKind.File, FilePath = png }));

        Assert.Null(result.Warning);
        Assert.Contains("--form-string 'note=@not-a-file;type=x/y'", result.Command);
        Assert.Contains($"-F 'avatar=@{png};type=image/png'", result.Command);
        Assert.DoesNotContain("Content-Type", result.Command);
    }

    [Theory]
    [InlineData(CurlExportStyle.Bash, "-F 'f=@\"C:\\\\a;b.pdf\";type=application/pdf'")]
    [InlineData(CurlExportStyle.PowerShell, "-F 'f=@\"C:\\\\a;b.pdf\";type=application/pdf'")]
    [InlineData(CurlExportStyle.Cmd, "-F \"f=@\"\"C:\\\\a;b.pdf\"\";type=application/pdf\"")]
    [InlineData(CurlExportStyle.WindowsPowerShell, "-F '\"f=@\"\"C:\\\\a;b.pdf\"\";type=application/pdf\"'")]
    public void Paths_with_curl_syntax_characters_are_quoted(CurlExportStyle style, string expected)
    {
        var result = Export(Form(new FormFieldData { Key = "f", Kind = FormFieldKind.File, FilePath = @"C:\a;b.pdf" }), style);
        Assert.Contains(expected, result.Command);
    }

    [Fact]
    public void Text_fields_with_a_type_are_quoted_and_types_with_parameters_become_headers()
    {
        var result = Export(Form(
            new() { Key = "meta", Value = "{\"a\":1}", ContentType = "application/json" },
            new() { Key = "doc", Value = "x", ContentType = "application/json; charset=utf-8" }));

        Assert.Contains("-F 'meta=\"{\\\"a\\\":1}\";type=application/json'", result.Command);
        Assert.Contains("-F 'doc=\"x\";headers=\"Content-Type: application/json; charset=utf-8\"'", result.Command);
    }

    [Fact]
    public void Warns_about_fields_curl_cannot_send()
    {
        var result = Export(Form(
            new() { Key = "a=b", Value = "1" },
            new() { Key = "doc", Kind = FormFieldKind.File, FilePath = _files.PathFor("gone.pdf") },
            new() { Key = "empty", Kind = FormFieldKind.File }));

        Assert.NotNull(result.Warning);
        Assert.Contains("'='", result.Warning);
        Assert.Contains("wasn't found", result.Warning);
        Assert.Contains("no file chosen", result.Warning);
        Assert.DoesNotContain("a=b=1", result.Command);

        Assert.Contains("no fields", Export(Form()).Warning);
    }

    [Fact]
    public void Binary_uses_data_binary_with_the_content_type()
    {
        var pdf = _files.Create("report.pdf", [1]);
        var result = Export(new RequestModel
        {
            Method = HttpMethodType.PUT,
            Url = "https://bucket.example.com/report.pdf",
            BodyMode = BodyMode.Binary,
            BinaryFilePath = pdf,
            FollowRedirects = false
        });

        Assert.Null(result.Warning);
        Assert.Contains("-H 'Content-Type: application/pdf'", result.Command);
        Assert.Contains($"--data-binary '@{pdf}'", result.Command);
    }

    [Fact]
    public void Url_encoded_exports_the_exact_encoded_body()
    {
        var request = Form(new FormFieldData { Key = "name", Value = "Ada Lovelace" }, new() { Key = "q", Value = "a&b" });
        request.BodyMode = BodyMode.FormUrlEncoded;

        Assert.Contains("--data-raw 'name=Ada+Lovelace&q=a%26b'", Export(request).Command);
        Assert.Contains("--data-raw \"name=Ada+Lovelace&q=a\"%\"26b\"", Export(request, CurlExportStyle.Cmd).Command);
    }
}

public class AiImportFormTests
{
    [Fact]
    public void Maps_form_fields_and_binary_paths()
    {
        var dto = new AiImportResponseDto
        {
            Method = "POST",
            Url = "https://api.example.com",
            BodyMode = "FormData",
            FormFields =
            [
                new AiFormFieldDto { Key = "name", Value = "Ada", Type = "text" },
                new AiFormFieldDto { Key = "avatar", Type = "FILE", FilePath = "panda.png", ContentType = "image/png" },
                null!
            ],
            BinaryFilePath = "ignored-unless-binary.bin"
        };

        var request = AiImportService.ConvertDtoToRequest(dto);

        Assert.Equal(BodyMode.FormData, request.BodyMode);
        Assert.Equal(2, request.FormFields.Count);
        Assert.Equal((FormFieldKind.File, "panda.png", "image/png"),
            (request.FormFields[1].Kind, request.FormFields[1].FilePath, request.FormFields[1].ContentType));
        Assert.Equal("ignored-unless-binary.bin", request.BinaryFilePath);
    }

    [Fact]
    public void Rejects_numeric_enum_values_and_reads_urlencoded_body_text_leniently()
    {
        Assert.Equal(BodyMode.None, AiImportService.ConvertDtoToRequest(new AiImportResponseDto { BodyMode = "7" }).BodyMode);

        var request = AiImportService.ConvertDtoToRequest(new AiImportResponseDto
        {
            Method = "POST",
            BodyMode = "FormUrlEncoded",
            BodyText = "name=John Doe&city=Cape%20Town"
        });

        Assert.Equal(BodyMode.FormUrlEncoded, request.BodyMode);
        Assert.Equal(["name=John Doe", "city=Cape Town"], request.FormFields.Select(f => $"{f.Key}={f.Value}"));
    }
}
