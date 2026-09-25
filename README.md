# ACD — Asistente de Conexión Documental (Document Connection Assistant)

Windows desktop agent that bridges a web application (MFD — Módulo de Firma Documental) with [FirmaONPE](https://www.gob.pe/onpe), the Peruvian national digital signing tool, through a local WebSocket server. Built for government document workflows where digital signatures carry legal weight.

## How It Works

ACD runs as a **system tray application** listening on `localhost:7272`. When a user initiates a digital signature from the MFD web app:

1. The browser connects to ACD via WebSocket and sends the PDF document.
2. ACD deposits the file in a watched directory (`C:\TFIRMA`) where FirmaONPE picks it up.
3. FirmaONPE signs the document using the user's digital certificate.
4. ACD detects the signed file and streams it back to the browser.
5. The browser uploads the signed PDF to the backend, which validates the signature and stores it in the document management system (SGD).

The agent is launched on demand via a custom URI scheme (`acd://`) registered in `HKCU\Software\Classes`, requiring no administrator privileges.

## WebSocket Protocol

Every session starts with `CONNECTED` (sent by ACD) followed by `AUTH` → `AUTH_OK`. `CONNECTED.capabilities` announces the supported operations: `pdf.sign.firma-onpe`, `pdf.open` and `document.edit` (`protocolVersion` stays `2`). A session runs a single operation type; ACD allows one active operation per type (`SESSION_BUSY`, close `4002`, otherwise). Unless stated otherwise, every `ERROR` frame (`code`, `message`, `category`) is followed by the socket closing.

### Document edit (`document.edit`)

Opens an editable document (default: `.docx`, configurable through `Acd:DocumentEdit:AllowedExtensions`) with the Windows default application and delivers the PDF the user saves next to it.

| Direction | Message | Fields |
|-----------|---------|--------|
| Web → ACD | `EDIT_DOCUMENT` + 1 binary frame | `requestId` (UUID), `filename`, `size`, `sha256` |
| ACD → Web | `DOCUMENT_OPENED` | `requestId` |
| ACD → Web | `EDITED_PDF_READY` (one per saved version) | `requestId`, `filename`, `size`, `version` |
| Web → ACD | `REQUEST_EDITED_PDF` | `requestId` |
| ACD → Web | `EDITED_PDF` + 1 binary message (latest version) | `requestId`, `filename`, `size` |
| Web → ACD | `CANCEL_EDIT` | `requestId` |
| ACD → Web | `EDIT_TIMEOUT` | `requestId` |

1. `EDIT_DOCUMENT` is validated before the binary frame is read: `size` above `MaxFileBytes` or a frame larger than the declared `size` is rejected with `INVALID_FILE_SIZE`.
2. The document is stored in `%LOCALAPPDATA%\ACD\Temp\DocumentEdit\<requestId>\`, the PDF watcher is armed on that folder and only then the document is opened.
3. Each valid PDF (`%PDF-` … `%%EOF`, stable on disk, new content) saved in that folder produces `EDITED_PDF_READY` with an increasing `version`.
4. `REQUEST_EDITED_PDF` may be sent any number of times; ACD answers with the latest version. The binary message is split in 64 KB fragments and its length always equals `EDITED_PDF.size`.
5. `CANCEL_EDIT` stops the watcher and closes the socket with `1000`. Closing the socket from the web side has the same effect.
6. After `TimeoutMinutes` (default 60) since `DOCUMENT_OPENED`, ACD sends `EDIT_TIMEOUT` and closes with `1000`.

The editor may stay open after the session ends, so the folder is never deleted on close; it is removed by retention (`RetentionHours`, default 24) the next time a document is stored.

| Code | Category | Fatal | When |
|------|----------|-------|------|
| `EDIT_INVALID_REQUEST` | `SYSTEM` | yes | Invalid `requestId`, `filename`, extension or `sha256` format; `requestId` not matching the active edit |
| `INVALID_FILE_SIZE` | `SYSTEM` | yes | `size` out of range or binary payload size mismatch |
| `EDIT_HASH_MISMATCH` | `SYSTEM` | yes | Binary payload does not match `sha256` |
| `STORAGE_LIMIT_EXCEEDED` | `TRANSIENT` | yes | `MaxStorageBytes` reached |
| `WRITE_FAILED` | `TRANSIENT` | yes | The document could not be stored |
| `EDIT_LAUNCH_FAILED` | `USER_ACTIONABLE` | yes | Windows could not open the document |
| `EDIT_PDF_NOT_READY` | `USER_ACTIONABLE` | no | `REQUEST_EDITED_PDF` before any PDF was saved |
| `READ_FAILED` | `TRANSIENT` | no* | The PDF is being written; retry `REQUEST_EDITED_PDF`. *Fatal if it fails after `EDITED_PDF` was sent |

## Tech Stack

| Component | Details |
|-----------|---------|
| Runtime | .NET 10 (`net10.0-windows`), self-contained `win-x64` |
| Server | ASP.NET Core Kestrel (WebSocket + HTTP health check) |
| UI | WinForms system tray icon |
| Logging | Serilog (console + rolling file) |
| Installer & Updates | [Velopack](https://velopack.io) 1.2.0 |
| CI/CD | GitHub Actions |

## Distribution Channels

Two variants coexist side-by-side on the same machine with independent installations, URI schemes, and mutex names:

| Variant | Pack ID | Channel | URI Scheme | Release Type |
|---------|---------|---------|------------|--------------|
| Production | `ACD` | `stable` | `acd://` | Stable |
| Development | `ACD-Dev` | `dev` | `acd-dev://` | Pre-release |

The variant is embedded at build time via `AssemblyMetadata` (`-p:AcdVariant=Prod|Dev`), so the installed binary knows its identity without relying on environment variables.

## Build & Run

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), [Velopack CLI](https://docs.velopack.io/) (`dotnet tool install -g vpk`)

```powershell
dotnet build src/ACD/ACD.csproj

# Run as Development (dev channel, acd-dev:// scheme)
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/ACD/ACD.csproj
```

## Release Pipeline

Releases are triggered by git tags and published to GitHub Releases via Velopack.

| Tag Pattern | Example | Workflow | Output |
|-------------|---------|----------|--------|
| `vX.Y.Z` | `v1.2.3` | `release-prod.yml` | Stable release, `stable` channel |
| `vX.Y.Z-dev.N` | `v1.2.3-dev.1` | `release-dev.yml` | Pre-release, `dev` channel |

Each workflow runs on `windows-latest` and follows the Velopack-recommended flow:

```
git tag → GitHub Actions
  ├─ dotnet publish (self-contained, win-x64)
  ├─ vpk download (fetch prior release feed for delta generation)
  ├─ vpk pack (create installer + delta packages)
  └─ vpk upload (publish to GitHub Releases)
```

The installer bundles the .NET 10 runtime — end users install nothing else.

## Auto-Update

A background service checks for updates every 6 hours (first check 60 seconds after startup). Updates are downloaded silently but only applied with explicit user action and never during an active signing session.

## Project Structure

```
src/ACD/
  Configuration/   — App and update options
  DocumentEdit/    — Document edit workflow: request validation, per-request storage, edited PDF watcher
  Files/           — Shared file utilities (stable file probe, shell launcher)
  Firma/           — File deposit and FirmaONPE file watcher
  System/          — Tray icon, single-instance guard, URI scheme registration
  Update/          — Background update service
  WebSocket/       — WebSocket middleware, session handler, protocol messages
  Program.cs
.github/workflows/
  release-dev.yml
  release-prod.yml
```

## License

[MIT](LICENSE)
