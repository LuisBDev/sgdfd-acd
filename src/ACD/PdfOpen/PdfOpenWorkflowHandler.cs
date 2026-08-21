using System.Security.Cryptography;
using ACD.Configuration;
using ACD.WebSocket;
using ACD.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.PdfOpen;

public sealed class PdfOpenWorkflowHandler
{
    private static readonly byte[] PdfHeader = "%PDF-"u8.ToArray();
    private readonly ILogger _logger;
    private readonly PdfOpenOptions _options;
    private readonly IPdfLauncher _pdfLauncher;
    private readonly PdfOpenStorage _storage;
    private readonly string _sessionId;
    private PdfOpenRequest? _request;

    public PdfOpenWorkflowHandler(
        PdfOpenOptions options,
        PdfOpenStorage storage,
        IPdfLauncher pdfLauncher,
        ILogger logger,
        string sessionId)
    {
        _options = options;
        _storage = storage;
        _pdfLauncher = pdfLauncher;
        _logger = logger;
        _sessionId = sessionId;
    }

    public bool HasPendingRequest => _request is not null;

    public async Task<SessionState> PrepareAsync(
        NativeWebSocket ws,
        OpenPdfMessage message,
        CancellationToken ct)
    {
        if (!PdfOpenRequestValidator.TryValidate(message, _options, out var request, out var code, out var detail))
        {
            await WebSocketTransport.SendErrorAndCloseAsync(ws, code, detail, 1008, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        _request = request;
        _logger.LogInformation(
            "[{SessionId}] OPEN_PDF aceptado: request {RequestId}, {Size} bytes",
            _sessionId,
            request!.RequestId,
            request.ExpectedSize);
        return SessionState.ReceivingPdfToOpen;
    }

    public async Task<SessionState> HandleBinaryFrameAsync(
        NativeWebSocket ws,
        byte[] data,
        CancellationToken ct)
    {
        var request = _request;
        _request = null;
        if (request is null)
        {
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.UnexpectedMessage, "No OPEN_PDF request is pending", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        if (data.LongLength != request.ExpectedSize)
        {
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.InvalidFileSize, "Binary payload size does not match OPEN_PDF metadata", 1008, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        if (data.Length < PdfHeader.Length || !data.AsSpan(0, PdfHeader.Length).SequenceEqual(PdfHeader))
        {
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.PdfInvalid, "Binary payload is not a PDF", 1008, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(data));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(request.ExpectedSha256)))
        {
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.HashMismatch, "PDF checksum does not match", 1008, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        string filePath;
        try
        {
            filePath = await _storage.SaveAsync(request, data, ct).ConfigureAwait(false);
        }
        catch (PdfOpenStorageLimitException)
        {
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.StorageLimitExceeded, "Temporary PDF storage limit exceeded", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[{SessionId}] No se pudo guardar el PDF temporal", _sessionId);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.WriteFailed, "Could not store the temporary PDF", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        try
        {
            _pdfLauncher.Open(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SessionId}] Windows no pudo abrir el PDF temporal", _sessionId);
            await WebSocketTransport.SendErrorAndCloseAsync(ws, ErrorCatalog.PdfOpenFailed, "Windows could not open the default PDF application", 1011, _logger, _sessionId, ct);
            return SessionState.Closed;
        }

        await WebSocketTransport.SendJsonAsync(
            ws,
            new PdfOpenedMessage(request.RequestId.ToString()),
            AcdJsonContext.Default.PdfOpenedMessage,
            ct);
        _logger.LogInformation("[{SessionId}] PDF_OPENED enviado para request {RequestId}", _sessionId, request.RequestId);
        return SessionState.Authenticated;
    }
}
