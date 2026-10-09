using System.ComponentModel;
using System.Net.WebSockets;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using ACD.WebSocket;
using ACD.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.DocumentWorkspace;

public sealed class WorkspaceWatchSession : IAsyncDisposable
{
    private const int InternalFailure = 1011;

    private readonly IWorkspaceWatcherFactory _factory;
    private readonly ILogger _logger;
    private readonly WorkspacePaths _paths;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _sessionId;
    private bool _closed;
    private int _disposed;
    private Task? _pump;
    private CancellationTokenSource? _pumpCts;
    private string? _requestId;
    private IWorkspaceWatcher? _watcher;

    public WorkspaceWatchSession(IWorkspaceWatcherFactory factory, WorkspacePaths paths, ILogger logger, string sessionId)
    {
        _factory = factory;
        _paths = paths;
        _logger = logger;
        _sessionId = sessionId;
    }

    public async Task<SessionState> StartAsync(NativeWebSocket ws, WatchWorkspaceMessage msg, CancellationToken ct)
    {
        if (!WorkspaceRequestValidation.TryResolveRemito(_paths, msg.RequestId, msg.Anio, msg.NumeroEmision, out var directory, out var rejection))
            return await RejectAsync(ws, rejection!, ct);

        _requestId = msg.RequestId!;
        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!TryStartWatcher(directory, _pumpCts.Token, out var watcher))
        {
            await ReleaseAsync();
            return await RejectAsync(ws, new WorkspaceRejection(ErrorCatalog.WorkspaceWatchFailed, "Could not watch the remito folder", InternalFailure), ct);
        }

        await SendAsync(ws, new WorkspaceWatchingMessage(_requestId, directory, ToPayload(watcher.Current)), AcdJsonContext.Default.WorkspaceWatchingMessage, ct);
        _pump = PumpAsync(ws, watcher.Changes, _pumpCts.Token, ct);
        _logger.LogInformation("[{SessionId}] WATCH_WORKSPACE aceptado: request {RequestId}, estado -> WatchingWorkspace", _sessionId, _requestId);
        return SessionState.WatchingWorkspace;
    }

    public async Task<SessionState> StopAsync(NativeWebSocket ws, StopWatchMessage msg, CancellationToken ct)
    {
        await ReleaseAsync();

        if (!string.Equals(msg.RequestId, _requestId, StringComparison.Ordinal))
            return await RejectAsync(ws, new WorkspaceRejection(ErrorCatalog.InvalidRequestId, "requestId does not match the active watch", WorkspaceRequestValidation.PolicyViolation), ct);

        await CloseOnceAsync(() => WebSocketTransport.CloseWebSocketAsync(ws, WebSocketCloseStatus.NormalClosure, MessageType.StopWatch, ct));
        _logger.LogInformation("[{SessionId}] STOP_WATCH recibido, cerrando la sesión", _sessionId);
        return SessionState.Closed;
    }

    public ValueTask DisposeAsync() => new(ReleaseAsync());

    private bool TryStartWatcher(string directory, CancellationToken ct, out IWorkspaceWatcher watcher)
    {
        watcher = _factory.Create(directory);
        _watcher = watcher;
        try
        {
            watcher.Start(ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            _logger.LogError(ex, "[{SessionId}] No se pudo vigilar la carpeta del remito", _sessionId);
            return false;
        }
    }

    private async Task PumpAsync(
        NativeWebSocket ws,
        ChannelReader<WordFileInfo?> changes,
        CancellationToken pumpToken,
        CancellationToken sendToken)
    {
        try
        {
            while (await changes.WaitToReadAsync(pumpToken).ConfigureAwait(false))
            {
                while (!pumpToken.IsCancellationRequested && changes.TryRead(out var snapshot))
                    await SendAsync(ws, new WorkspaceChangedMessage(_requestId!, ToPayload(snapshot)), AcdJsonContext.Default.WorkspaceChangedMessage, sendToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] No se pudo enviar WORKSPACE_CHANGED; se detiene la vigilancia", _sessionId);
        }
    }

    private async Task ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (_pumpCts is { } pumpCts)
            await pumpCts.CancelAsync().ConfigureAwait(false);
        if (_pump is { } pump)
            await pump.ConfigureAwait(false);
        if (_watcher is { } watcher)
            await watcher.DisposeAsync().ConfigureAwait(false);

        _pumpCts?.Dispose();
    }

    private async Task SendAsync<T>(NativeWebSocket ws, T message, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_closed)
                await WebSocketTransport.SendJsonAsync(ws, message, typeInfo, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CloseOnceAsync(Func<Task> close)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) return;
            _closed = true;
            await close().ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<SessionState> RejectAsync(NativeWebSocket ws, WorkspaceRejection rejection, CancellationToken ct)
    {
        await CloseOnceAsync(() => WebSocketTransport.SendErrorAndCloseAsync(ws, rejection.Code, rejection.Message, rejection.CloseCode, _logger, _sessionId, ct));
        return SessionState.Closed;
    }

    private static LatestWordPayload? ToPayload(WordFileInfo? word) =>
        word is null
            ? null
            : new LatestWordPayload(word.FileName, WorkspaceFileNames.FormatLocal(word.ChangedAt), word.Size, word.Sha256);
}
