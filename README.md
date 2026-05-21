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
- **Bamboo-simple storage** — saved requests, history, autosave, and settings are plain local JSON.
- **Curl tamer** — import, export, copy, and reshape requests without losing the little details.
- **AI import helper** — paste messy snippets and let the panda sort the payload from the leaves.
- **X-ray vision** — flip to RAW mode and watch the panda crack open the connection: DNS, TCP, TLS handshake, certs, and the bytes on the wire.

## Features

### Request building
- **All HTTP verbs** — `GET`, `POST`, `PUT`, `DELETE`, `PATCH`, `HEAD`, `OPTIONS`.
- **Tabbed builder** for **Params**, **Headers**, **Auth**, and **Body** with per-row enable/disable checkboxes (toggle a header off without deleting it).
- **Body modes**: None, Raw (text/plain), JSON, XML, Form-URL-Encoded — content-type is set automatically.
- **Auth modes**: None, Bearer token, Basic (username/password, base64-encoded), API Key (configurable header name, defaults to `X-API-Key`).
- **JSON-aware editor** powered by AvalonEdit: syntax highlighting, line numbers, configurable font size, optional word wrap.
- **Per-request timeout** and **follow-redirects** toggle.
- **Optional SSL certificate bypass** for self-signed dev environments (off by default).

### Response viewing
- Three response tabs: **Pretty** (auto-formatted JSON with syntax highlighting), **Raw**, and **Headers** (sortable grid).
- **Status color coding** — 2xx green, 4xx amber, 5xx red — with reason phrase, duration in ms, and response size.
- **Cancel in-flight request** at any time (proper `CancellationToken` plumbing on `HttpClient`).
- **Copy response body** to clipboard with one click.

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
- **Phase-by-phase timings** — DNS resolution, TCP connect, TLS handshake, and time-to-first-byte, drawn as proportional bars so the slow step is obvious at a glance.
- **The endpoint, demystified** — every resolved IP, the address actually connected to, and the local/remote socket endpoints.
- **Full TLS detail** — negotiated protocol (e.g. TLS 1.3), the exact cipher suite, and the validation result.
- **The certificate chain** — leaf + intermediates as cards, each with subject, issuer, validity window, SANs, signature algorithm, serial, and a green / amber / red badge (valid / expiring soon / expired).
- **The bytes on the wire** — the exact raw request sent and the raw response head received.
- The familiar **Pretty / Raw / Headers** tabs still populate as usual, so you lose nothing by switching modes.

Built for the curious and the stuck:
- **See where it breaks.** If a connection fails, the Connection tab stays populated up to the failure and flags the exact phase — so you instantly know whether it's DNS, the TCP connect, or the TLS handshake that died, not just "request failed".
- **Inspect even bad certificates.** The handshake records validation errors but doesn't reject, so you can examine expired or self-signed certs. The **SSL certificate verification** setting decides whether a bad cert is a hard error or just a noted warning.
- **What you see is what's on the wire** — raw mode does *not* auto-decompress `gzip`/`deflate` bodies (size and headers are still accurate), and reuses your headers, auth, query params, and body exactly as composed.

> Raw mode is a per-session toggle; saved requests and history work in both modes.

### Saved requests & history
- **Saved request library** with create / load / rename / duplicate / delete — each request stored as its own JSON file under `%AppData%\PayloadPanda\requests\`.
- **Autosave** of the active request with a 2-second debounce, restored on next launch.
- **Request history** — every send is logged with timestamp, method, URL, status, and duration. Click any entry to reload the exact request snapshot. History is capped (default 500, configurable) and can be cleared.
- **Smart history → saved-request linking**: if a history entry came from a saved request that still exists, reloading takes you back to the live saved request rather than the snapshot.

### Import / export
- **Import / export request as JSON** via standard file dialogs — share requests via git, Slack, or wherever.
- **Copy as cURL** — turns the current request (verb, URL, headers, auth, body) into a ready-to-paste `curl` command.
- **AI Import** — paste a curl command, OpenAPI/Swagger snippet, code sample, or plain-English description, and let an LLM extract a structured request (method, URL, headers, query params, body, auth). Preview the parsed JSON before applying. Uses any OpenAI-compatible Chat Completions endpoint, with configurable model (defaults include `gpt-5-nano`, `gpt-5-mini`, `gpt-5`, `gpt-5.2`, `gpt-5.4-nano`, `gpt-5.4-mini`).

### UI / UX
- **Custom dark chrome** with borderless window, draggable title bar, and proper Windows snap/maximize behavior (handles `WM_GETMINMAXINFO` so maximize respects the work area).
- **Three-pane layout**: Saved Requests / History (left), Request Builder (center), Response Viewer (right/bottom).
- **`Enter` in the URL bar sends the request** — no mouse needed for the common case.
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
```

Or open `Source\PayloadPanda.sln` in Visual Studio and press F5.

### First-run config
- (Optional) **AI Import**: open Settings and paste your OpenAI API key (or point `AI Endpoint` at any OpenAI-compatible URL — Azure OpenAI, local LLM gateways, etc.).
- **History file path**: defaults to `%AppData%\PayloadPanda\history.json`. Override in Settings to share history across machines via OneDrive/Dropbox.

## Project layout

```
Source/
  PayloadPanda.sln
  PayloadPanda.csproj
  App.xaml(.cs)          # App bootstrap, DI of services + MainViewModel
  MainWindow.xaml(.cs)   # Custom chrome, AvalonEdit wiring
  AssemblyInfo.cs

  Models/                # RequestModel, ResponseModel, SettingsModel,
                         # SavedRequest, HistoryItem, HeaderItem,
                         # QueryParamItem, AiImportResult, Enums,
                         # ConnectionDiagnostics (raw-socket diagnostics)
  ViewModels/            # MainViewModel — central VM, all RelayCommands
  Views/                 # AiImportPanel, RenameDialog, SettingsWindow
  Services/              # HttpService, RawSocketService, PersistenceService,
                         # SavedRequestService, AiImportService
  Converters/            # WPF value converters
  Helpers/               # BindingProxy
  Themes/                # DarkTheme.xaml
  images/                # app.ico
```

Architecture is plain MVVM with a Services layer. `MainViewModel` orchestrates the UI; each service has a single concern (HTTP, raw-socket diagnostics, persistence, saved requests, AI parsing). `RawSocketService` mirrors `HttpService`'s signature, so the view-model swaps transports with a single switch and the response tabs work unchanged.

## Where your data lives

| What | Where |
| --- | --- |
| Saved requests | `%AppData%\PayloadPanda\requests\<guid>.json` (one file per request) |
| Autosave | `%AppData%\PayloadPanda\autosave.json` |
| History | `%AppData%\PayloadPanda\history.json` (or your custom path) |
| Settings | `%AppData%\PayloadPanda\settings.json` |

Everything is plain, human-readable, indented JSON — easy to back up, diff, or check into a personal repo.

## License

See [LICENSE](LICENSE).
