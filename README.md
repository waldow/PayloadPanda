<div align="center">
  <img src="Source/images/app-preview.png" alt="PayloadPanda panda mascot icon" width="168" />
  <h1>PayloadPanda</h1>
  <p><strong>A fast, keyboard-friendly REST API client for Windows.</strong></p>
  <p>Feed it URLs, headers, bodies, and cURL. PayloadPanda pads through the request jungle, sends the payload, and brings the response back neatly groomed.</p>
</div>

Postman-lite, built natively in WPF on .NET 9 — no Electron, no account, no cloud sync. Local JSON files all the way down.

## Panda magic

<img src="Source/images/svgforicons.svg" alt="PayloadPanda panda icon" width="96" align="right" />

- **Payloads with paws** — compose requests quickly without waking up a heavyweight client.
- **Bamboo-simple storage** — saved requests, history, open tabs, and settings are plain local JSON.
- **Curl tamer** — copy any request as a ready-to-run `curl` command for Bash, PowerShell or cmd.exe, quoting and all.
- **AI import helper** — paste messy snippets and let the panda sort the payload from the leaves.
- **CORS detective** — add browser-style `Origin` headers, run preflight checks, and see exactly why a browser would block a response.
- **X-ray vision** — flip to RAW mode and watch the panda crack open the connection: DNS, TCP, TLS handshake, certs, and the bytes on the wire.

## Features

### Request building
- **All HTTP verbs** — `GET`, `POST`, `PUT`, `DELETE`, `PATCH`, `HEAD`, `OPTIONS`.
- **Tabbed builder** for **Params**, **Headers**, **Auth**, and **Body** with per-row enable/disable checkboxes (toggle a header off without deleting it).
- **Body modes**: None, Raw (text/plain), JSON, XML, Form-URL-Encoded — content-type is set automatically, and a `Content-Type` row in **Headers** overrides it (e.g. `application/vnd.api+json`). `GET`/`HEAD` never send a body.
- **Query params** from the **Params** grid are appended to the URL; a grid row replaces a same-named parameter typed into the URL, and repeated keys (`tag=a&tag=b`) are all sent.
- **Sensible defaults** — `User-Agent: PayloadPanda/1.0` and `Accept: */*` unless you set your own.
- **Auth modes**: None, Bearer token, Basic (username/password, base64-encoded), API Key (configurable header name, defaults to `X-API-Key`).
- **JSON-aware editor** powered by AvalonEdit: syntax highlighting, line numbers, configurable font size, optional word wrap.
- **Per-request timeout** and **follow-redirects** toggle.
- **Optional SSL certificate bypass** for self-signed dev environments (off by default).

### Response viewing
- Three response tabs: **Pretty** (auto-formatted JSON with syntax highlighting — non-ASCII and `<>&` shown as-is), **Raw**, and **Headers** (sortable grid, one row per header line so repeated `Set-Cookie`s stay separate).
- Bodies are decoded using the response's `charset` (UTF-8 by default); responses over 64 MB are truncated, and the status bar says so.
- **Status color coding** — 2xx green, 4xx amber, 5xx red — with reason phrase, duration in ms, and response size.
- **Cancel in-flight request** at any time (proper `CancellationToken` plumbing on `HttpClient`).
- **Copy response body** to clipboard, or **download** it to a file.

### CORS troubleshooting
- **Browser-style Origin testing** — enable CORS testing in the **Options** tab and PayloadPanda injects the configured `Origin` header into sends without permanently adding it to saved request headers.
- **Preflight probe** — send an `OPTIONS` request with only the browser CORS preflight headers: `Origin`, `Access-Control-Request-Method`, and `Access-Control-Request-Headers`.
- **Automatic requested-header detection** — leave `Access-Control-Request-Headers` blank to derive the preflight header list from the current request, including auth headers, API key headers, and non-simple body content types like JSON/XML.
- **Credential checks** — toggle credentialed mode to verify `Access-Control-Allow-Credentials: true` and catch invalid wildcard-origin responses.
- **Plain-English verdicts** — the CORS panel checks status, allowed origin, allowed method, allowed headers, credentials, and max-age, then reports pass/fail with the exact missing or mismatched header.
- **Browser-accurate rules** — origins must match exactly (case included), `*` is literal for credentialed requests, `Authorization` is never covered by `Access-Control-Allow-Headers: *`, and safelisted methods (`GET`/`HEAD`/`POST`) needn't be listed.

> CORS analysis models browser rules; the desktop client itself is not blocked by CORS, so the verdict tells you what would happen from frontend JavaScript.

### Low-Level Connect (raw socket mode) 🔬

Flip the **`HTTP | RAW`** pill in the URL bar and PayloadPanda stops using `HttpClient` — instead it opens the connection by hand over a raw `TcpClient` (wrapped in `SslStream` for HTTPS), writes a hand-built HTTP/1.1 request, and reports **everything `HttpClient` normally hides**. It's a network inspector and an API client in the same window.

A new **Connection** response tab appears, led by a color-coded **timing waterfall**:

```
DNS   ▓▓ 12 ms
TCP   ▓▓▓▓ 31 ms
TLS   ▓▓▓▓▓▓▓ 88 ms
TTFB  ▓▓▓ 24 ms
──────────────────
TLS 1.3 · TLS_AES_256_GCM_SHA384
✔ cert valid · expires 2026-08-14
```

What you get on every raw send:
- **Phase-by-phase timings** — DNS resolution, TCP connect, TLS handshake, and time-to-first-byte (the server's wait once the request was sent), drawn as proportional bars so the slow step is obvious at a glance.
- **The endpoint, demystified** — every resolved IP, the address actually connected to, and the local/remote socket endpoints.
- **Full TLS detail** — negotiated protocol (e.g. TLS 1.3), the exact cipher suite, and the validation result.
- **The certificate chain** — leaf + intermediates as cards, each with subject, issuer, validity window, SANs, signature algorithm, serial, and a green / amber / red badge (valid / expiring soon / expired).
- **The bytes on the wire** — the exact raw request sent and the raw response head received (including any interim `100`/`103` responses).
- The familiar **Pretty / Raw / Headers** tabs still populate as usual, so you lose nothing by switching modes.

Built for the curious and the stuck:
- **See where it breaks.** If a connection fails, the Connection tab stays populated up to the failure and flags the exact phase — so you instantly know whether it's DNS, the TCP connect, or the TLS handshake that died, not just "request failed".
- **Inspect even bad certificates.** The handshake records validation errors but doesn't reject, so you can examine expired or self-signed certs. The **SSL certificate verification** setting decides whether a bad cert is a hard error or just a noted warning.
- **What you see is what's on the wire** — raw mode does *not* auto-decompress `gzip`/`deflate` bodies (size and headers are still accurate), and reuses your headers, auth, query params, and body exactly as composed.

> Raw mode is a per-tab toggle (remembered across restarts); saved requests and history work in both modes.

### Saved requests & history
- **Saved request library** with create / load / rename / duplicate / delete — each request stored as its own JSON file under `%AppData%\PayloadPanda\requests\`. Click an entry to open it (or switch to the tab where it's already open); `Enter` opens the selected one.
- **Saving is explicit** — **Save Current** writes the active tab to the library. Sending never changes a saved request, so you can experiment freely; edited tabs show a `*` until saved.
- **Tabs survive restarts** — open tabs, including unsaved drafts, are saved about a second after each change and restored on next launch.
- **Request history** — every send is logged with timestamp, method, URL, status, and duration. Double-click an entry to reopen the exact request that was sent; if it came from a saved request that still exists, the tab stays linked to it, so **Save Current** updates that entry. History is capped (default 500, configurable) and can be cleared.

### Import / export
- **Import / export request as JSON** via standard file dialogs — share requests via git, Slack, or wherever.
- **Copy as cURL** — turns the current request exactly as it would be sent (verb, URL with params, headers, auth, body, and CORS Origin when enabled) into a ready-to-paste `curl` command. Pick the shell in Settings: **Bash**, **PowerShell 7.3+**, **Windows PowerShell 5.1** or **cmd.exe** — each gets its own quoting, so quotes, `&`, `%`, `$` and backslashes survive the trip. cmd.exe can't carry line breaks, so multi-line JSON bodies are minified (other multi-line bodies get spaces, with a note in the status bar).
- **AI Import** — paste a curl command, OpenAPI/Swagger snippet, code sample, or plain-English description, and let an LLM extract a structured request (method, URL, headers, query params, body, auth). Preview the parsed JSON before applying. Uses any OpenAI-compatible Chat Completions endpoint (note that the pasted text is sent to it), with configurable model (defaults include `gpt-5-nano`, `gpt-5-mini`, `gpt-5`, `gpt-5.2`, `gpt-5.4-nano`, `gpt-5.4-mini`).

### UI / UX
- **Custom dark chrome** with borderless window, draggable title bar, and proper Windows snap/maximize behavior (handles `WM_GETMINMAXINFO` so maximize respects the work area).
- **Three-pane layout**: Saved Requests / History (left), Request Builder (center), Response Viewer (right/bottom).
- **`Enter` in the URL bar sends the request** — no mouse needed for the common case. `Ctrl+T` / `Ctrl+W` open and close tabs, `Ctrl+Tab` / `Ctrl+Shift+Tab` switch between them.
- **In-app Settings window** for HTTP defaults, editor preferences, AI provider config, and history settings (including a custom history file path).
- **Status bar** with rolling messages ("Sent", "Cancelled", "Saved", "Imported", etc.).

## Tech stack

| Area | Choice |
| --- | --- |
| Runtime | .NET 9 (`net9.0-windows`), WPF |
| Language | C# 13, nullable reference types, implicit usings |
| MVVM | [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) |
| Code editor | [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) |
| HTTP | `System.Net.Http.HttpClient` with `CancellationToken` |
| Raw transport | `TcpClient` + `SslStream` (`System.Net.Sockets` / `System.Net.Security`) for Low-Level Connect mode |
| Serialization | `System.Text.Json` (camelCase, indented) |
| Persistence | JSON files under `%AppData%\PayloadPanda\` |
| Tests | xUnit (`Source/PayloadPanda.Tests`), run in CI on every push |

No external services, no telemetry, no database.

## Getting started

### Prerequisites
- Windows 10/11
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 (17.14+) **or** the `dotnet` CLI

### Build & run

```powershell
cd Source
dotnet build               # Debug
dotnet build -c Release    # Release
dotnet run                 # Launch the app
dotnet test PayloadPanda.Tests/PayloadPanda.Tests.csproj   # Run the tests
```

The test project compiles the app's `Models/` and `Services/` directly and targets plain `net9.0`, so the tests also run on Linux/macOS.

Or open `Source\PayloadPanda.sln` in Visual Studio and press F5.

### First-run config
- (Optional) **AI Import**: open Settings and paste your OpenAI API key (or point `AI Endpoint` at any OpenAI-compatible URL — Azure OpenAI, local LLM gateways, etc.). The `OPENAI_API_KEY` environment variable is used as a fallback, but only for the default OpenAI endpoint.
- **History file path**: defaults to `%AppData%\PayloadPanda\history.json`. Override in Settings to keep history elsewhere, e.g. a synced folder. Pointing at an existing file adopts its history (it is never overwritten), and several app instances can share one file — entries are merged on every save. Encrypted credentials inside history entries only decrypt for the Windows user who saved them; on another machine those fields come back empty.

## Project layout

```
Source/
  PayloadPanda.sln
  PayloadPanda.csproj
  App.xaml(.cs)          # App bootstrap, services + MainViewModel, startup load order
  MainWindow.xaml(.cs)   # Custom chrome, saved/history panes, request tabs
  AssemblyInfo.cs

  Models/                # RequestModel, ResponseModel, SettingsModel,
                         # SavedRequest, HistoryItem, RequestTabSession,
                         # HeaderItem, QueryParamItem, AiImportResult, Enums,
                         # ConnectionDiagnostics, CorsAnalysis
  ViewModels/            # MainViewModel (tabs, library, history, settings)
                         # RequestWorkspaceViewModel (one per request tab)
  Views/                 # RequestWorkspaceView, AiImportPanel, RenameDialog, SettingsWindow
  Services/              # RequestComposer, HttpService, RawSocketService,
                         # CurlExporter, CorsAnalyzer, PersistenceService,
                         # SavedRequestService, RequestTabSessionService,
                         # AiImportService, RequestSecrets, ...
  Converters/            # WPF value converters
  Helpers/               # BindingProxy
  Themes/                # DarkTheme.xaml
  images/                # app.ico
  PayloadPanda.Tests/    # xUnit tests
```

Architecture is plain MVVM with a Services layer. `MainViewModel` owns the tabs, the saved-request library and history; each tab is a `RequestWorkspaceViewModel`. `RequestComposer` is the single place that turns a request into its final URL, headers and body, and `HttpService`, `RawSocketService` and `CurlExporter` all build on it — so the two transports and the cURL export always agree on what a request means.

## Where your data lives

| What | Where |
| --- | --- |
| Saved requests | `%AppData%\PayloadPanda\requests\<guid>.json` (one file per request) |
| Open tabs | `%AppData%\PayloadPanda\tabs.json` |
| History | `%AppData%\PayloadPanda\history.json` (or your custom path) |
| Settings | `%AppData%\PayloadPanda\settings.json` |
| Error log | `%AppData%\PayloadPanda\errors.log` (unexpected errors, if any) |

Everything is human-readable, indented JSON — easy to back up, diff, or check into a personal repo. The exception is credentials: auth fields, the values of well-known credential headers and query parameters (`Authorization`, `Cookie`, `*token*`, `api_key`, ...), and the AI API key are encrypted with Windows DPAPI for your user account. Secrets anywhere else (a request body, a query string typed into the URL) are stored as plain text. Exported request files are always plain text, so they can be shared.

## License

See [LICENSE](LICENSE).
