using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.Tests.DocumentWorkspace;

internal sealed class RecordingWebSocket : NativeWebSocket
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<string> _pending = Channel.CreateUnbounded<string>();
    private readonly List<string> _sent = [];
    private readonly object _gate = new();
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeStatusDescription;
    private WebSocketState _state = WebSocketState.Open;

    public override WebSocketCloseStatus? CloseStatus
    {
        get
        {
            lock (_gate) return _closeStatus;
        }
    }

    public override string? CloseStatusDescription
    {
        get
        {
            lock (_gate) return _closeStatusDescription;
        }
    }

    public override WebSocketState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public override string? SubProtocol => null;

    public IReadOnlyList<string> SentFrames
    {
        get
        {
            lock (_gate) return [.. _sent];
        }
    }

    public async Task<JsonElement> NextFrameAsync()
    {
        using var timeout = new CancellationTokenSource(FrameTimeout);
        var text = await _pending.Reader.ReadAsync(timeout.Token);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state != WebSocketState.Open)
                throw new InvalidOperationException("The WebSocket is not open");
            if (messageType != WebSocketMessageType.Text) return Task.CompletedTask;

            var text = Encoding.UTF8.GetString(buffer);
            _sent.Add(text);
            _pending.Writer.TryWrite(text);
        }

        return Task.CompletedTask;
    }

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
        }

        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) =>
        CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override void Abort()
    {
        lock (_gate) _state = WebSocketState.Aborted;
    }

    public override void Dispose()
    {
    }
}
