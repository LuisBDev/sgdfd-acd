using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using ACD.Configuration;
using ACD.Files;
using ACD.WebSocket;
using ACD.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.DocumentWorkspace;

public sealed class DocumentWorkspaceHandler
{
    private const string LocalDateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private const string WordExtension = ".docx";
    private const int PolicyViolation = 1008;
    private const int InternalFailure = 1011;

    private readonly ILogger _logger;
    private readonly DocumentEditOptions _options;
    private readonly WorkspacePaths _paths;
    private readonly string _sessionId;
    private readonly IShellLauncher _shellLauncher;
    private readonly WorkspaceFileStore _store;
    private PendingWrite? _pending;

    public DocumentWorkspaceHandler(
        DocumentEditOptions options,
        WorkspacePaths paths,
        WorkspaceFileStore store,
        IShellLauncher shellLauncher,
        ILogger logger,
        string sessionId)
    {
        _options = options;
        _paths = paths;
        _store = store;
        _shellLauncher = shellLauncher;
        _logger = logger;
        _sessionId = sessionId;
    }

    public bool HasPendingRequest => _pending is not null;

    public long? PendingSize => _pending?.Size;

    public async Task<SessionState> SendStatusAsync(NativeWebSocket ws, WorkspaceStatusMessage message, CancellationToken ct)
    {
        if (!TryResolveRemito(message.RequestId, message.Anio, message.NumeroEmision, out var directory, out var rejection))
            return await RejectAsync(ws, rejection!, ct);

        WorkspaceStatus status;
        try
        {
            status = _store.GetStatus(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await RejectIoAsync(ws, ex, "Could not read the remito folder", ct);
        }

        var latestWord = status.LatestWord is { } word
            ? new LatestWordPayload(word.FileName, FormatLocal(word.LastWriteTime), word.Size, word.Sha256)
            : null;
        await WebSocketTransport.SendJsonAsync(
            ws,
            new WorkspaceStatusResultMessage(message.RequestId!, status.FolderExists, latestWord),
            AcdJsonContext.Default.WorkspaceStatusResultMessage,
            ct);
        return await CompleteAsync(ws, MessageType.WorkspaceStatusResult, ct);
    }

    public async Task<SessionState> PrepareWriteWordAsync(NativeWebSocket ws, WriteWordMessage message, CancellationToken ct)
    {
        if (!TryResolveRemito(message.RequestId, message.Anio, message.NumeroEmision, out var directory, out var rejection)
            || !TryValidateWordName(message.Filename, out rejection)
            || !TryValidateDeclaredContent(message.Size, message.Sha256, out rejection))
            return await RejectAsync(ws, rejection!, ct);

        _pending = new PendingWrite(PendingKind.Word, message.RequestId!, directory, message.Filename!, message.Size, message.Sha256!, message.Open);
        _logger.LogInformation("[{SessionId}] WRITE_WORD aceptado: request {RequestId}, {Size} bytes", _sessionId, message.RequestId, message.Size);
        return SessionState.ReceivingWorkspaceFile;
    }

    public async Task<SessionState> PrepareWritePdfCopyAsync(NativeWebSocket ws, WritePdfCopyMessage message, CancellationToken ct)
    {
        if (!TryResolveRemito(message.RequestId, message.Anio, message.NumeroEmision, out var directory, out var rejection)
            || !TryValidateWordName(message.WordFilename, out rejection)
            || !TryValidateDeclaredContent(message.Size, message.Sha256, out rejection))
            return await RejectAsync(ws, rejection!, ct);

        _pending = new PendingWrite(PendingKind.PdfCopy, message.RequestId!, directory, message.WordFilename!, message.Size, message.Sha256!, false);
        _logger.LogInformation("[{SessionId}] WRITE_PDF_COPY aceptado: request {RequestId}, {Size} bytes", _sessionId, message.RequestId, message.Size);
        return SessionState.ReceivingWorkspaceFile;
    }

    public async Task<SessionState> HandleBinaryFrameAsync(NativeWebSocket ws, byte[] data, CancellationToken ct)
    {
        var pending = _pending;
        _pending = null;
        if (pending is null)
            return await RejectAsync(ws, new Rejection(ErrorCatalog.UnexpectedMessage, "No workspace write is pending", InternalFailure), ct);

        if (!MatchesDeclaredContent(pending, data))
            return await RejectAsync(ws, new Rejection(ErrorCatalog.WorkspaceIntegrity, "Binary payload does not match the declared size and sha256", PolicyViolation), ct);

        return pending.Kind == PendingKind.Word
            ? await WriteWordAsync(ws, pending, data, ct)
            : await WritePdfCopyAsync(ws, pending, data, ct);
    }

    public async Task<SessionState> SendWordAsync(NativeWebSocket ws, ReadWordMessage message, CancellationToken ct)
    {
        if (!TryResolveRemito(message.RequestId, message.Anio, message.NumeroEmision, out var directory, out var rejection)
            || !TryValidateWordName(message.Filename, out rejection))
            return await RejectAsync(ws, rejection!, ct);

        WordFileInfo info;
        byte[] content;
        try
        {
            (info, content) = await _store.ReadWordAsync(directory, message.Filename!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return await RejectAsync(ws, new Rejection(ErrorCatalog.WorkspaceFileNotFound, "The Word document does not exist in the remito folder", PolicyViolation), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await RejectIoAsync(ws, ex, "Could not read the Word document", ct);
        }

        if (info.Size > _options.MaxFileBytes)
            return await RejectAsync(ws, new Rejection(ErrorCatalog.InvalidFileSize, $"Document size must not exceed {_options.MaxFileBytes} bytes", PolicyViolation), ct);

        await WebSocketTransport.SendJsonAsync(
            ws,
            new WordContentMessage(message.RequestId!, info.FileName, info.Size, info.Sha256, FormatLocal(info.LastWriteTime)),
            AcdJsonContext.Default.WordContentMessage,
            ct);
        await ws.SendAsync(content, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        return await CompleteAsync(ws, MessageType.WordContent, ct);
    }

    public async Task<SessionState> OpenFolderAsync(NativeWebSocket ws, OpenFolderMessage message, CancellationToken ct)
    {
        if (!TryResolveRemito(message.RequestId, message.Anio, message.NumeroEmision, out var directory, out var rejection))
            return await RejectAsync(ws, rejection!, ct);

        if (!Directory.Exists(directory))
            return await RejectAsync(ws, new Rejection(ErrorCatalog.WorkspaceFolderNotFound, "The remito folder does not exist", PolicyViolation), ct);

        try
        {
            _shellLauncher.OpenFolder(directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] Windows no pudo abrir la carpeta del remito", _sessionId);
            return await RejectAsync(ws, new Rejection(ErrorCatalog.ProcessStartFailed, "Windows could not open the remito folder", InternalFailure), ct);
        }

        await WebSocketTransport.SendJsonAsync(ws, new FolderOpenedMessage(message.RequestId!), AcdJsonContext.Default.FolderOpenedMessage, ct);
        return await CompleteAsync(ws, MessageType.FolderOpened, ct);
    }

    private async Task<SessionState> WriteWordAsync(NativeWebSocket ws, PendingWrite pending, byte[] data, CancellationToken ct)
    {
        WordFileInfo info;
        try
        {
            info = await _store.WriteWordAsync(pending.Directory, pending.FileName, data, ct).ConfigureAwait(false);
        }
        catch (WorkspaceFileExistsException)
        {
            return await RejectAsync(ws, new Rejection(ErrorCatalog.WorkspaceFileExists, "A Word document with that name already exists in the remito folder", PolicyViolation), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await RejectIoAsync(ws, ex, "Could not write the Word document", ct);
        }

        if (pending.Open && !TryOpenWord(Path.Combine(pending.Directory, info.FileName)))
            return await RejectAsync(ws, new Rejection(ErrorCatalog.EditLaunchFailed, "The Word document was saved but Windows could not open it", InternalFailure), ct);

        await WebSocketTransport.SendJsonAsync(
            ws,
            new WordWrittenMessage(pending.RequestId, info.FileName, FormatLocal(info.LastWriteTime)),
            AcdJsonContext.Default.WordWrittenMessage,
            ct);
        return await CompleteAsync(ws, MessageType.WordWritten, ct);
    }

    private async Task<SessionState> WritePdfCopyAsync(NativeWebSocket ws, PendingWrite pending, byte[] data, CancellationToken ct)
    {
        (string FileName, bool Renamed) copy;
        try
        {
            copy = await _store.WritePdfCopyAsync(pending.Directory, pending.FileName, data, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await RejectIoAsync(ws, ex, "Could not write the PDF copy", ct);
        }

        await WebSocketTransport.SendJsonAsync(
            ws,
            new PdfCopyWrittenMessage(pending.RequestId, copy.FileName, copy.Renamed),
            AcdJsonContext.Default.PdfCopyWrittenMessage,
            ct);
        return await CompleteAsync(ws, MessageType.PdfCopyWritten, ct);
    }

    private bool TryOpenWord(string path)
    {
        try
        {
            _shellLauncher.Open(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] Windows no pudo abrir el Word del remito", _sessionId);
            return false;
        }
    }

    private bool TryResolveRemito(string? requestId, string? anio, string? numeroEmision, out string directory, out Rejection? rejection)
    {
        directory = string.Empty;
        if (!Guid.TryParse(requestId, out _))
        {
            rejection = new Rejection(ErrorCatalog.InvalidRequestId, "requestId must be a valid UUID", PolicyViolation);
            return false;
        }

        if (!_paths.TryGetRemitoDirectory(anio!, numeroEmision!, out directory))
        {
            rejection = new Rejection(ErrorCatalog.WorkspaceInvalidKey, "anio must have 4 digits and numeroEmision 1 to 10 digits", PolicyViolation);
            return false;
        }

        rejection = null;
        return true;
    }

    private static bool TryValidateWordName(string? fileName, out Rejection? rejection)
    {
        rejection = fileName is not null && WorkspaceFileNames.IsValidFileName(fileName, WordExtension)
            ? null
            : new Rejection(ErrorCatalog.WorkspaceInvalidFilename, "filename must be a safe .docx file name", PolicyViolation);
        return rejection is null;
    }

    private bool TryValidateDeclaredContent(long size, string? sha256, out Rejection? rejection)
    {
        if (size <= 0 || size > _options.MaxFileBytes)
            rejection = new Rejection(ErrorCatalog.InvalidFileSize, $"Document size must be between 1 and {_options.MaxFileBytes} bytes", PolicyViolation);
        else if (sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
            rejection = new Rejection(ErrorCatalog.WorkspaceIntegrity, "sha256 must contain 64 hexadecimal characters", PolicyViolation);
        else
            rejection = null;
        return rejection is null;
    }

    private static bool MatchesDeclaredContent(PendingWrite pending, byte[] data) =>
        data.LongLength == pending.Size
        && string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), pending.Sha256, StringComparison.OrdinalIgnoreCase);

    private async Task<SessionState> RejectIoAsync(NativeWebSocket ws, Exception ex, string message, CancellationToken ct)
    {
        _logger.LogError(ex, "[{SessionId}] Fallo de E/S en la carpeta del remito", _sessionId);
        return await RejectAsync(ws, new Rejection(ErrorCatalog.WorkspaceIoFailed, message, InternalFailure), ct);
    }

    private async Task<SessionState> RejectAsync(NativeWebSocket ws, Rejection rejection, CancellationToken ct)
    {
        await WebSocketTransport.SendErrorAndCloseAsync(ws, rejection.Code, rejection.Message, rejection.CloseCode, _logger, _sessionId, ct);
        return SessionState.Closed;
    }

    private async Task<SessionState> CompleteAsync(NativeWebSocket ws, string response, CancellationToken ct)
    {
        _logger.LogInformation("[{SessionId}] {Response} enviado, cerrando la sesión", _sessionId, response);
        await WebSocketTransport.CloseWebSocketAsync(ws, WebSocketCloseStatus.NormalClosure, response, ct);
        return SessionState.Closed;
    }

    private static string FormatLocal(DateTime value) =>
        value.ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);

    private enum PendingKind
    {
        Word,
        PdfCopy
    }

    private sealed record PendingWrite(PendingKind Kind, string RequestId, string Directory, string FileName, long Size, string Sha256, bool Open);

    private sealed record Rejection(string Code, string Message, int CloseCode);
}
