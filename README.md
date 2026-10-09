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

Every session starts with `CONNECTED` (sent by ACD) followed by `AUTH` → `AUTH_OK`. `CONNECTED.capabilities` announces the supported operations: `pdf.sign.firma-onpe`, `pdf.open`, `document.workspace` and `document.workspace.watch` (`protocolVersion` stays `2`). A session runs a single operation type; ACD allows one active operation per type (`SESSION_BUSY`, close `4002`, otherwise). Watch sessions are the exception: they take no operation slot (see below). Every `ERROR` frame (`code`, `message`, `category`) is terminal: ACD closes the socket right after it. Recoverable conditions use their own message types.

### Document workspace (`document.workspace`)

Reads and writes the Word document and the PDF copy of a remito in its folder on the device, and converts a `.docx` to PDF with Microsoft Word. Each command is a session of its own: `AUTH` → `AUTH_OK`, the command, its response, then ACD closes with `1000`.

The remito folder is `<root>\<anio>\<numeroEmision>`, where `<root>` is the user's Documents folder (`SpecialFolder.MyDocuments`, so a OneDrive redirection is honored) plus `TDOCUMENTOS\MPD`; `Acd:DocumentWorkspace:RootDirectory` overrides it (environment variables are expanded). `anio` must be 4 digits and `numeroEmision` 1 to 10 digits; it is used as sent, leading zeros included. Otherwise ACD answers `WORKSPACE_INVALID_KEY`. `filename` must be a safe name: no paths or `..`, at most 180 characters, exact extension. The "Word of the remito" is the `*.docx` in the folder with the latest effective date `changedAt = max(CreationTime, LastWriteTime)`, excluding `~$*` lock files, so a Word pasted into the folder becomes active even if its `LastWriteTime` is older. On a tie, the name that sorts last wins (ordinal, case-insensitive), which picks the most recent `_<YYYYMMDD-HHMMSS>` suffix.

For binary payloads the JSON declares `size` (1 to `Acd:DocumentWorkspace:MaxFileBytes`, 20 MiB by default) and `sha256` (64 hexadecimal characters), and the binary frame follows immediately. Timestamps are local time without zone (`yyyy-MM-ddTHH:mm:ss`).

| Direction | Message | Fields |
|-----------|---------|--------|
| Web → ACD | `WRITE_WORD` + 1 binary frame | `requestId` (UUID), `anio`, `numeroEmision`, `filename`, `size`, `sha256`, `open` |
| ACD → Web | `WORD_WRITTEN` | `requestId`, `filename`, `changedAt`, `opened` |
| Web → ACD | `READ_WORD` | `requestId`, `anio`, `numeroEmision`, `filename` |
| ACD → Web | `WORD_CONTENT` + 1 binary frame | `requestId`, `filename`, `size`, `sha256`, `changedAt` |
| Web → ACD | `CONVERT_TO_PDF` + 1 binary frame | `requestId`, `size`, `sha256` |
| ACD → Web | `PDF_CONTENT` + 1 binary frame | `requestId`, `size`, `sha256` |
| Web → ACD | `WRITE_PDF_COPY` + 1 binary frame | `requestId`, `anio`, `numeroEmision`, `wordFilename`, `size`, `sha256` |
| ACD → Web | `PDF_COPY_WRITTEN` | `requestId`, `filename`, `renamed` |
| Web → ACD | `OPEN_FOLDER` | `requestId`, `anio`, `numeroEmision` |
| ACD → Web | `FOLDER_OPENED` | `requestId` |

1. `WRITE_WORD` creates the file with `CreateNew` and never overwrites: an existing name returns `WORKSPACE_FILE_EXISTS`. The folder is created when missing. With `open: true`, ACD launches the file with the Windows default application. The launch result is reported in `opened`, which is also `false` when `open` is `false`. The file stays in the folder either way, and a failed launch is not an `ERROR`.
2. `READ_WORD` opens the file with read and delete sharing, so it works while Word has the document open. A local `.docx` over `MaxFileBytes` returns `INVALID_FILE_SIZE` on `READ_WORD`.
3. `WRITE_PDF_COPY` names the PDF after `wordFilename` with the `.pdf` extension. If that PDF is locked by another program, ACD writes `<name> (2).pdf` and answers `renamed: true`. Otherwise it overwrites the existing PDF.
4. `CONVERT_TO_PDF` runs in its own slot: it does not block the other workspace commands, but only one conversion runs at a time. A second conversion while one is active is rejected with `SESSION_BUSY` (close `4002`); it is not queued. Word runs hidden, with alerts and macros off, and opens the document read-only. ACD only acts on the Word instance it created for that conversion, never on Word instances that were already running. If the user opens a document while the conversion runs and Windows routes it into that instance, ACD leaves the instance open and visible instead of quitting it. On timeout, or if Word stops responding, ACD ends the instance it created even if it holds such a document. The timeout is `Acd:DocumentWorkspace:ConversionTimeoutSeconds` (default 90).
5. The workspace commands other than `CONVERT_TO_PDF` share one slot, so a second one while another is active returns `SESSION_BUSY` (close `4002`).
6. A binary payload that does not match the declared `size` or `sha256`, or a frame larger than the declared size, returns `WORKSPACE_INTEGRITY`.

#### Watching the remito folder (`document.workspace.watch`)

A watch session stays open and pushes the Word of the remito every time it changes: `AUTH` → `AUTH_OK`, `WATCH_WORKSPACE`, then `WORKSPACE_WATCHING` and any number of `WORKSPACE_CHANGED` until the web sends `STOP_WATCH`, which ACD answers by closing with `1000`.

| Direction | Message | Fields |
|-----------|---------|--------|
| Web → ACD | `WATCH_WORKSPACE` | `requestId` (UUID), `anio`, `numeroEmision` |
| ACD → Web | `WORKSPACE_WATCHING` | `requestId`, `folder` (absolute path of the remito folder), `latestWord` or `null` |
| ACD → Web (push) | `WORKSPACE_CHANGED` | `requestId`, `latestWord` or `null` |
| Web → ACD | `STOP_WATCH` | `requestId`. ACD closes with `1000` |

- `latestWord` is `{ filename, changedAt, size, sha256 }`: the Word of the remito as defined above, `changedAt` its effective date in local time (`yyyy-MM-ddTHH:mm:ss`) and `sha256` in lowercase hexadecimal. `null` means the folder has no Word.
- `WATCH_WORKSPACE` creates the remito folder when it is missing. `WORKSPACE_WATCHING` carries the initial snapshot; every push carries the same `requestId`.
- Changes are debounced (`Acd:DocumentWorkspace:WatchDebounceMilliseconds`, default 1000) and wait for the file to be stable, so a Word save through a temporary file and a rename yields one final push. A snapshot equal to the last one sent is not pushed again.
- While watching, ACD keeps reading the socket: only `STOP_WATCH` and the WebSocket close are accepted. A `STOP_WATCH` with another `requestId` returns `INVALID_REQUEST_ID`; any other message returns `UNEXPECTED_MESSAGE`.
- Watch sessions take no operation slot: several tabs can watch at the same time, within the global limit of 5 connections, and they never block the workspace commands. They do not count as active work for auto-update either, so an update may close them; the web reconnects and the new `WORKSPACE_WATCHING` corrects its state.

`ERROR` codes (all terminal):

| Code | Category | When |
|------|----------|------|
| `INVALID_REQUEST_ID` | `SYSTEM` | `requestId` is not a UUID, or `STOP_WATCH` carries a `requestId` other than the watched one |
| `WORKSPACE_INVALID_KEY` | `USER_ACTIONABLE` | `anio` or `numeroEmision` has an invalid format |
| `WORKSPACE_INVALID_FILENAME` | `USER_ACTIONABLE` | `filename` or `wordFilename` is not a safe `.docx` name |
| `INVALID_FILE_SIZE` | `SYSTEM` | Declared `size` outside 1 to `MaxFileBytes`, a local `.docx` over the limit on `READ_WORD`, or a converted PDF over the limit |
| `WORKSPACE_INTEGRITY` | `TRANSIENT` | `sha256` is not 64 hexadecimal characters, or the binary payload does not match the declared `size` or `sha256` |
| `WORKSPACE_FILE_EXISTS` | `USER_ACTIONABLE` | `WRITE_WORD`: a file with that name already exists in the folder |
| `WORKSPACE_FILE_NOT_FOUND` | `USER_ACTIONABLE` | `READ_WORD`: the file does not exist in the folder |
| `WORKSPACE_FOLDER_NOT_FOUND` | `USER_ACTIONABLE` | `OPEN_FOLDER`: the remito folder does not exist |
| `WORKSPACE_IO_FAILED` | `TRANSIENT` | The remito folder or a file could not be read or written |
| `PROCESS_START_FAILED` | `USER_ACTIONABLE` | `OPEN_FOLDER`: Windows could not start Explorer |
| `WORD_NOT_INSTALLED` | `USER_ACTIONABLE` | `CONVERT_TO_PDF`: Microsoft Word is not installed |
| `CONVERSION_TIMEOUT` | `TRANSIENT` | Word did not finish the conversion within `ConversionTimeoutSeconds` |
| `CONVERSION_FAILED` | `SYSTEM` | Word failed, or did not produce a valid PDF |
| `WORKSPACE_WATCH_FAILED` | `SYSTEM` | `WATCH_WORKSPACE`: ACD could not create or watch the remito folder (close `1011`) |
| `SESSION_BUSY` | `TRANSIENT` | Another workspace command or conversion is active (close `4002`) |

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
  DocumentWorkspace/ — Remito workspace: folder paths, Word and PDF copy files, Word to PDF conversion
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
