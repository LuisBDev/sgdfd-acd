using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using ACD.DocumentWorkspace;
using ACD.Firma;
using ACD.Firma.Signing;
using ACD.PdfOpen;
using ACD.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

public sealed class AcdSessionHandler
{
    private const int ProtocolVersion = 2;
    private static readonly string[] Capabilities = ["pdf.sign.firma-onpe", "pdf.open", "document.workspace", "document.workspace.watch"];
    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private readonly DocumentWorkspaceHandler _documentWorkspaceHandler;
    private readonly FirmaWorkflowHandler _firmaHandler;
    private readonly ILogger _logger;
    private readonly PdfOpenWorkflowHandler _pdfOpenHandler;
    private readonly ISessionGate _sessionGate;
    private readonly string _sessionId;
    private readonly string _watchDirectory;
    private readonly WorkspaceWatchSession _workspaceWatchSession;
    private string? _authToken;
    private SessionOperation? _operation;

    private SessionState _state = SessionState.Connected;

    public AcdSessionHandler(
        FirmaWorkflowHandler firmaHandler,
        PdfOpenWorkflowHandler pdfOpenHandler,
        DocumentWorkspaceHandler documentWorkspaceHandler,
        WorkspaceWatchSession workspaceWatchSession,
        ISessionGate sessionGate,
        ILogger logger,
        string sessionId,
        string watchDirectory)
    {
        _firmaHandler = firmaHandler;
        _pdfOpenHandler = pdfOpenHandler;
        _documentWorkspaceHandler = documentWorkspaceHandler;
        _workspaceWatchSession = workspaceWatchSession;
        _sessionGate = sessionGate;
        _logger = logger;
        _sessionId = sessionId;
        _watchDirectory = watchDirectory;
    }

    public async Task HandleAsync(NativeWebSocket webSocket, CancellationToken ct)
    {
        _logger.LogInformation("[{SessionId}] Sesión iniciada", _sessionId);

        try
        {
            var connected = new ConnectedMessage(
                AgentVersion,
                "READY",
                _watchDirectory,
                ProtocolVersion,
                "windows",
                Capabilities);
            await WebSocketTransport.SendJsonAsync(webSocket, connected, AcdJsonContext.Default.ConnectedMessage, ct);

            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var maxFrameBytes = _state == SessionState.ReceivingWorkspaceFile
                    ? _documentWorkspaceHandler.PendingSize
                    : null;
                var (kind, payload) = await WebSocketTransport.ReceiveFrameAsync(webSocket, ct, maxFrameBytes);

                if (kind == FrameKind.Close)
                {
                    _logger.LogInformation("[{SessionId}] El cliente cerró la conexión", _sessionId);
                    break;
                }

                if (kind == FrameKind.TooLarge)
                {
                    _logger.LogWarning("[{SessionId}] Frame mayor al tamaño declarado en el comando del workspace", _sessionId);
                    await WebSocketTransport.SendErrorAndCloseAsync(webSocket, ErrorCatalog.WorkspaceIntegrity, "Frame exceeds the declared size", 1008, _logger, _sessionId, ct);
                    return;
                }

                if (kind == FrameKind.Binary)
                {
                    if (_state == SessionState.ReceivingWorkspaceFile && _documentWorkspaceHandler.HasPendingRequest)
                    {
                        _state = await _documentWorkspaceHandler.HandleBinaryFrameAsync(webSocket, payload!, ct);
                        continue;
                    }

                    if (_state == SessionState.ReceivingPdfToOpen && _pdfOpenHandler.HasPendingRequest)
                    {
                        _state = await _pdfOpenHandler.HandleBinaryFrameAsync(webSocket, payload!, ct);
                        continue;
                    }

                    if (_state != SessionState.ReceivingFile || _firmaHandler.CurrentFilename is null)
                    {
                        _logger.LogWarning("[{SessionId}] Se recibió un frame binario en estado inesperado {State}", _sessionId, _state);
                        await SendErrorAndCloseAsync(webSocket, ErrorCatalog.UnexpectedMessage, "Binary frame received in wrong state", 1011, ct);
                        return;
                    }

                    _state = await _firmaHandler.HandleBinaryFrameAsync(webSocket, payload!, ct);
                    continue;
                }

                if (payload is null) continue;

                var baseMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.BaseMessage);
                if (baseMsg is null)
                {
                    await SendErrorAndCloseAsync(webSocket, ErrorCatalog.UnknownMessageType, "Could not parse message type", 1011, ct);
                    return;
                }

                await DispatchTextMessageAsync(webSocket, baseMsg.Type, payload, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{SessionId}] Sesión cancelada", _sessionId);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogWarning("[{SessionId}] Conexión WebSocket cerrada prematuramente", _sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] Excepción no controlada en el manejador de sesión", _sessionId);
            try
            {
                await _workspaceWatchSession.DisposeAsync();
                await WebSocketTransport.SendErrorAndCloseAsync(webSocket, ErrorCatalog.InternalError, ex.Message, 1011, _logger, _sessionId, ct);
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            await _workspaceWatchSession.DisposeAsync();

            if (_operation is { } operation)
            {
                _sessionGate.Release(operation);
                _operation = null;
                _logger.LogInformation("[{SessionId}] Operación {Operation} liberada", _sessionId, operation);
            }

            _state = SessionState.Closed;
            _logger.LogInformation("[{SessionId}] Sesión finalizada", _sessionId);
        }
    }

    private async Task DispatchTextMessageAsync(
        NativeWebSocket webSocket,
        string messageType,
        byte[] payload,
        CancellationToken ct)
    {
        switch (_state, messageType)
        {
            case (SessionState.Connected, MessageType.Auth):
                var authMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.AuthMessage);
                if (authMsg is null) break;
                if (string.IsNullOrWhiteSpace(authMsg.Token))
                {
                    await WebSocketTransport.SendErrorAndCloseAsync(webSocket, ErrorCatalog.AuthRequired, "A non-empty authentication token is required", 1008, _logger, _sessionId, ct);
                    return;
                }
                _authToken = authMsg.Token;
                _state = SessionState.Authenticated;
                _logger.LogInformation("[{SessionId}] AUTH recibido, estado -> Authenticated", _sessionId);
                await WebSocketTransport.SendJsonAsync(webSocket, new AuthOkMessage(), AcdJsonContext.Default.AuthOkMessage, ct);
                break;

            case (SessionState.Authenticated, MessageType.OpenPdf):
                var openPdfMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.OpenPdfMessage);
                if (openPdfMsg is null) break;
                if (!await TryBeginOperationAsync(webSocket, SessionOperation.PdfOpen, ct)) return;
                _state = await _pdfOpenHandler.PrepareAsync(webSocket, openPdfMsg, ct);
                break;

            case (SessionState.Authenticated, MessageType.WriteWord):
                var writeWordMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.WriteWordMessage);
                if (writeWordMsg is null) break;
                if (!await TryBeginOperationAsync(webSocket, SessionOperation.Workspace, ct)) return;
                _state = await _documentWorkspaceHandler.PrepareWriteWordAsync(webSocket, writeWordMsg, ct);
                break;

            case (SessionState.Authenticated, MessageType.ReadWord):
                var readWordMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.ReadWordMessage);
                if (readWordMsg is null) break;
                if (!await TryBeginOperationAsync(webSocket, SessionOperation.Workspace, ct)) return;
                _state = await _documentWorkspaceHandler.SendWordAsync(webSocket, readWordMsg, ct);
                break;

            case (SessionState.Authenticated, MessageType.WritePdfCopy):
                var writePdfCopyMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.WritePdfCopyMessage);
                if (writePdfCopyMsg is null) break;
                if (!await TryBeginOperationAsync(webSocket, SessionOperation.Workspace, ct)) return;
                _state = await _documentWorkspaceHandler.PrepareWritePdfCopyAsync(webSocket, writePdfCopyMsg, ct);
                break;

            case (SessionState.Authenticated, MessageType.OpenFolder):
                var openFolderMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.OpenFolderMessage);
                if (openFolderMsg is null) break;
                if (!await TryBeginOperationAsync(webSocket, SessionOperation.Workspace, ct)) return;
                _state = await _documentWorkspaceHandler.OpenFolderAsync(webSocket, openFolderMsg, ct);
                break;

            case (SessionState.Authenticated, MessageType.ConvertToPdf):
                var convertMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.ConvertToPdfMessage);
                if (convertMsg is null) break;
                if (!await TryBeginOperationAsync(webSocket, SessionOperation.Conversion, ct)) return;
                _state = await _documentWorkspaceHandler.PrepareConvertToPdfAsync(webSocket, convertMsg, ct);
                break;

            case (SessionState.Authenticated, MessageType.WatchWorkspace):
                var watchMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.WatchWorkspaceMessage);
                if (watchMsg is null) break;
                _state = await _workspaceWatchSession.StartAsync(webSocket, watchMsg, ct);
                break;

            case (SessionState.WatchingWorkspace, MessageType.StopWatch):
                var stopWatchMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.StopWatchMessage);
                if (stopWatchMsg is null) break;
                _state = await _workspaceWatchSession.StopAsync(webSocket, stopWatchMsg, ct);
                break;

            case (SessionState.Connected, _):
                _logger.LogWarning("[{SessionId}] Se recibió {Type} antes de AUTH", _sessionId, messageType);
                await WebSocketTransport.SendErrorAndCloseAsync(webSocket, ErrorCatalog.AuthRequired, "Authentication required before sending data", 1008, _logger, _sessionId, ct);
                return;

            case (SessionState.Authenticated, MessageType.PdfDownload):
                var pdfMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.PdfDownloadMessage);
                if (pdfMsg is null) break;
                if (!FirmaTipo.IsSupported(pdfMsg.Tipo))
                {
                    var code = string.IsNullOrEmpty(pdfMsg.Tipo) ? ErrorCatalog.MissingFirmaTipo : ErrorCatalog.UnsupportedFirmaTipo;
                    await WebSocketTransport.SendErrorAndCloseAsync(webSocket, code, $"Invalid firma type: {pdfMsg.Tipo ?? "(none)"}", 1011, _logger, _sessionId, ct);
                    return;
                }
                if (!await TryBeginOperationAsync(webSocket, SessionOperation.Signing, ct)) return;

                _firmaHandler.SetDocumentMetadata(pdfMsg.TipoDocumento, pdfMsg.NumeroDocumento, pdfMsg.Tipo, pdfMsg.Numeracion);
                _state = SessionState.ReceivingFile;
                _logger.LogInformation(
                    "[{SessionId}] Metadatos PDF_DOWNLOAD recibidos: {Tipo} {Numero} ({Size} bytes, tipoFirma {TipoFirma}) -> {Filename}, estado -> ReceivingFile",
                    _sessionId, pdfMsg.TipoDocumento, pdfMsg.NumeroDocumento, pdfMsg.Size, pdfMsg.Tipo, _firmaHandler.CurrentFilename);
                break;

            case (SessionState.WatchingFirma, MessageType.RequestSignedFile):
                var reqMsg = JsonSerializer.Deserialize(payload, AcdJsonContext.Default.RequestSignedFileMessage);
                if (reqMsg is null) break;
                _state = SessionState.SendingFile;
                _logger.LogInformation("[{SessionId}] REQUEST_SIGNED_FILE recibido, estado -> SendingFile", _sessionId);
                _state = await _firmaHandler.SendSignedFileAsync(webSocket, reqMsg.Filename, ct);
                return;

            default:
                _logger.LogWarning(
                    "[{SessionId}] Tipo de mensaje inesperado {Type} en estado {State}",
                    _sessionId, messageType, _state);
                var errorCode = IsKnownMessageType(messageType) ? ErrorCatalog.UnexpectedMessage : ErrorCatalog.UnknownMessageType;
                await SendErrorAndCloseAsync(webSocket, errorCode, $"Message type {messageType} not valid in state {_state}", 1011, ct);
                return;
        }
    }

    private async Task<bool> TryBeginOperationAsync(
        NativeWebSocket webSocket,
        SessionOperation operation,
        CancellationToken ct)
    {
        if (_operation == operation) return true;

        if (_operation is not null)
        {
            await WebSocketTransport.SendErrorAndCloseAsync(
                webSocket,
                ErrorCatalog.UnexpectedMessage,
                "A WebSocket session cannot change its operation type",
                1008,
                _logger,
                _sessionId,
                ct);
            return false;
        }

        if (!await _sessionGate.TryAcquireAsync(operation, ct))
        {
            _logger.LogWarning(
                "[{SessionId}] Operación {Operation} rechazada porque ya existe otra del mismo tipo",
                _sessionId,
                operation);
            await WebSocketTransport.SendErrorAndCloseAsync(
                webSocket,
                ErrorCatalog.SessionBusy,
                operation switch
                {
                    SessionOperation.Signing => "Another signing operation is already active",
                    SessionOperation.Workspace => "Another document workspace operation is already active",
                    SessionOperation.Conversion => "Another Word to PDF conversion is already active",
                    _ => "Another PDF opening operation is already active"
                },
                4002,
                _logger,
                _sessionId,
                ct);
            return false;
        }

        _operation = operation;
        _logger.LogInformation("[{SessionId}] Operación {Operation} adquirida", _sessionId, operation);
        return true;
    }

    private async Task SendErrorAndCloseAsync(
        NativeWebSocket webSocket,
        string code,
        string message,
        int closeCode,
        CancellationToken ct)
    {
        await _workspaceWatchSession.DisposeAsync();
        await WebSocketTransport.SendErrorAndCloseAsync(webSocket, code, message, closeCode, _logger, _sessionId, ct);
    }

    private static bool IsKnownMessageType(string type)
    {
        return type is MessageType.Auth or MessageType.PdfDownload or MessageType.OpenPdf or MessageType.RequestSignedFile
            or MessageType.Connected or MessageType.PdfReceived or MessageType.FirmaDisponible
            or MessageType.PdfOpened or MessageType.SignedFile or MessageType.FirmaTimeout
            or MessageType.WriteWord or MessageType.ReadWord or MessageType.WritePdfCopy or MessageType.OpenFolder or MessageType.ConvertToPdf
            or MessageType.WordWritten or MessageType.WordContent or MessageType.PdfCopyWritten or MessageType.FolderOpened or MessageType.PdfContent
            or MessageType.WatchWorkspace or MessageType.StopWatch or MessageType.WorkspaceWatching or MessageType.WorkspaceChanged
            or MessageType.Error;
    }
}
