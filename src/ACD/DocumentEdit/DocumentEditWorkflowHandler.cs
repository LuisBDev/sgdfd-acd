using System.Net.WebSockets;
using ACD.Configuration;
using ACD.Files;
using ACD.WebSocket;
using ACD.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.DocumentEdit;

public sealed class DocumentEditWorkflowHandler : IAsyncDisposable
{
    private const int FileBufferSize = 64 * 1024;

    private readonly ILogger _logger;
    private readonly DocumentEditOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _sessionId;
    private readonly IShellLauncher _shellLauncher;
    private readonly DocumentEditStorage _storage;
    private readonly IEditedPdfWatcherFactory _watcherFactory;
    private DocumentEditRequest? _activeRequest;
    private bool _closed;
    private CancellationTokenSource? _editCts;
    private DocumentEditRequest? _pendingRequest;
    private Task? _pumpTask;
    private IEditedPdfWatcher? _watcher;

    public DocumentEditWorkflowHandler(
        DocumentEditOptions options,
        DocumentEditStorage storage,
        IEditedPdfWatcherFactory watcherFactory,
        IShellLauncher shellLauncher,
        ILogger logger,
        string sessionId)
    {
        _options = options;
        _storage = storage;
        _watcherFactory = watcherFactory;
        _shellLauncher = shellLauncher;
        _logger = logger;
        _sessionId = sessionId;
    }

    public bool HasPendingRequest => _pendingRequest is not null;

    public long? PendingSize => _pendingRequest?.ExpectedSize;

    public async Task<SessionState> PrepareAsync(
        NativeWebSocket ws,
        EditDocumentMessage message,
        CancellationToken ct)
    {
        if (!DocumentEditRequestValidator.TryValidate(
                message.RequestId,
                message.Filename,
                message.Size,
                message.Sha256,
                _options,
                out var request,
                out var code,
                out var detail))
        {
            var errorCode = code == ErrorCatalog.InvalidFileSize ? code : ErrorCatalog.EditInvalidRequest;
            await WebSocketTransport.SendErrorAndCloseAsync(ws, errorCode, detail, 1008, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        _pendingRequest = request;
        _logger.LogInformation(
            "[{SessionId}] EDIT_DOCUMENT aceptado: request {RequestId}, {Size} bytes",
            _sessionId,
            request!.RequestId,
            request.ExpectedSize);
        return SessionState.ReceivingDocumentToEdit;
    }

    public async Task<SessionState> HandleBinaryFrameAsync(
        NativeWebSocket ws,
        byte[] data,
        CancellationToken ct)
    {
        var request = _pendingRequest;
        _pendingRequest = null;
        if (request is null)
        {
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.UnexpectedMessage, "No EDIT_DOCUMENT request is pending", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        if (!DocumentEditRequestValidator.TryVerifyContent(request, data, out var code, out var detail))
        {
            var errorCode = code == ErrorCatalog.HashMismatch ? ErrorCatalog.EditHashMismatch : code;
            await WebSocketTransport.SendErrorAndCloseAsync(ws, errorCode, detail, 1008, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        DocumentEditWorkspace workspace;
        try
        {
            workspace = await _storage.SaveSourceAsync(request, data, ct).ConfigureAwait(false);
        }
        catch (DocumentEditStorageLimitException)
        {
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.StorageLimitExceeded, "Temporary document storage limit exceeded", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[{SessionId}] No se pudo guardar el documento a editar", _sessionId);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.WriteFailed, "Could not store the document to edit", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        var editCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = _watcherFactory.Create(workspace);
        try
        {
            watcher.Start(editCts.Token);
            _shellLauncher.Open(workspace.SourcePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] Windows no pudo abrir el documento a editar", _sessionId);
            await watcher.DisposeAsync();
            editCts.Dispose();
            _storage.DeleteWorkspaceBestEffort(workspace.RequestId);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.EditLaunchFailed, "Windows could not open the document editor", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        _watcher = watcher;
        _editCts = editCts;
        _activeRequest = request;

        var requestId = request.RequestId.ToString();
        await WebSocketTransport.SendJsonAsync(
            ws,
            new DocumentOpenedMessage(requestId),
            AcdJsonContext.Default.DocumentOpenedMessage,
            ct);
        _logger.LogInformation("[{SessionId}] DOCUMENT_OPENED enviado para request {RequestId}", _sessionId, request.RequestId);

        _pumpTask = PumpVersionsAsync(ws, watcher, requestId, editCts.Token);
        return SessionState.EditingDocument;
    }

    public async Task<SessionState> SendEditedPdfAsync(
        NativeWebSocket ws,
        RequestEditedPdfMessage message,
        CancellationToken ct)
    {
        if (!IsActiveRequest(message.RequestId, out var requestId))
        {
            await SendErrorAndCloseAsync(ws, ErrorCatalog.EditInvalidRequest, "requestId does not match the active document edit", 1008, ct);
            return SessionState.Closed;
        }

        var version = _watcher?.LatestVersion;
        if (version is null)
        {
            await SendErrorAsync(ws, ErrorCatalog.EditPdfNotReady, "No edited PDF has been saved yet", ct);
            return SessionState.EditingDocument;
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed) return SessionState.Closed;

            FileStream stream;
            try
            {
                stream = new FileStream(version.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "[{SessionId}] El PDF editado {File} no está disponible para lectura", _sessionId, version.FileName);
                await SendErrorUnlockedAsync(ws, ErrorCatalog.ReadFailed, "The edited PDF is being written; try again", ct);
                return SessionState.EditingDocument;
            }

            await using (stream)
            {
                var length = stream.Length;
                if (length == 0)
                {
                    await SendErrorUnlockedAsync(ws, ErrorCatalog.ReadFailed, "The edited PDF is being written; try again", ct);
                    return SessionState.EditingDocument;
                }

                await WebSocketTransport.SendJsonAsync(
                    ws,
                    new EditedPdfMessage(requestId, version.FileName, length),
                    AcdJsonContext.Default.EditedPdfMessage,
                    ct);

                try
                {
                    await WebSocketTransport.SendStreamAsync(ws, stream, length, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "[{SessionId}] Error al enviar el PDF editado {File}", _sessionId, version.FileName);
                    _closed = true;
                    await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.ReadFailed, "Error reading the edited PDF", 1011, _logger, _sessionId, ct);
                    return SessionState.Closed;
                }

                _logger.LogInformation(
                    "[{SessionId}] EDITED_PDF enviado: {File} ({Size} bytes), versión {Version}",
                    _sessionId, version.FileName, length, version.Version);
            }
        }
        finally
        {
            _sendLock.Release();
        }

        return SessionState.EditingDocument;
    }

    public async Task<SessionState> CancelAsync(
        NativeWebSocket ws,
        CancelEditMessage message,
        CancellationToken ct)
    {
        if (!IsActiveRequest(message.RequestId, out _))
        {
            await SendErrorAndCloseAsync(ws, ErrorCatalog.EditInvalidRequest, "requestId does not match the active document edit", 1008, ct);
            return SessionState.Closed;
        }

        _logger.LogInformation("[{SessionId}] CANCEL_EDIT recibido para request {RequestId}", _sessionId, _activeRequest!.RequestId);
        await StopAsync();

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_closed)
            {
                _closed = true;
                await WebSocketTransport.CloseWebSocketAsync(ws, WebSocketCloseStatus.NormalClosure, "Edit cancelled", ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }

        return SessionState.Closed;
    }

    public async Task SendErrorAndCloseAsync(
        NativeWebSocket ws,
        string code,
        string message,
        int closeCode,
        CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed) return;
            _closed = true;
            await WebSocketTransport.SendErrorAndCloseAsync(ws, code, message, closeCode, _logger, _sessionId, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task StopAsync()
    {
        var editCts = _editCts;
        _editCts = null;
        if (editCts is not null)
        {
            try
            {
                await editCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var pumpTask = _pumpTask;
        _pumpTask = null;
        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[{SessionId}] El envío de versiones del PDF editado terminó con error", _sessionId);
            }
        }

        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null) await watcher.DisposeAsync();

        editCts?.Dispose();
    }

    private async Task PumpVersionsAsync(
        NativeWebSocket ws,
        IEditedPdfWatcher watcher,
        string requestId,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.TimeoutMinutes)));

        try
        {
            await foreach (var version in watcher.Versions.ReadAllAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_closed) return;
                    await WebSocketTransport.SendJsonAsync(
                        ws,
                        new EditedPdfReadyMessage(requestId, version.FileName, version.Size, version.Version),
                        AcdJsonContext.Default.EditedPdfReadyMessage,
                        ct);
                }
                finally
                {
                    _sendLock.Release();
                }

                _logger.LogInformation(
                    "[{SessionId}] EDITED_PDF_READY enviado: {File} ({Size} bytes), versión {Version}",
                    _sessionId, version.FileName, version.Size, version.Version);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await SendTimeoutAsync(ws, requestId, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[{SessionId}] Envío de versiones del PDF editado cancelado", _sessionId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "[{SessionId}] El socket se cerró mientras se notificaban versiones del PDF editado", _sessionId);
        }
    }

    private async Task SendTimeoutAsync(NativeWebSocket ws, string requestId, CancellationToken ct)
    {
        _logger.LogWarning("[{SessionId}] La edición del request {RequestId} agotó el tiempo de espera", _sessionId, requestId);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed) return;
            _closed = true;
            await WebSocketTransport.SendJsonAsync(ws, new EditTimeoutMessage(requestId), AcdJsonContext.Default.EditTimeoutMessage, ct);
            await WebSocketTransport.CloseWebSocketAsync(ws, WebSocketCloseStatus.NormalClosure, "Edit timeout", ct);
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "[{SessionId}] No se pudo notificar EDIT_TIMEOUT", _sessionId);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendErrorAsync(NativeWebSocket ws, string code, string message, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendErrorUnlockedAsync(ws, code, message, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendErrorUnlockedAsync(NativeWebSocket ws, string code, string message, CancellationToken ct)
    {
        if (_closed) return;
        await WebSocketTransport.SendJsonAsync(
            ws,
            new ErrorMessage(code, message, ErrorCatalog.CategoryOf(code)),
            AcdJsonContext.Default.ErrorMessage,
            ct);
    }

    private bool IsActiveRequest(string? requestId, out string activeRequestId)
    {
        activeRequestId = _activeRequest?.RequestId.ToString() ?? string.Empty;
        return _activeRequest is not null
               && Guid.TryParse(requestId, out var parsed)
               && parsed == _activeRequest.RequestId;
    }
}
