using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ACD.WebSocket.Messages;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

public static class WebSocketTransport
{
    private const int BufferSize = 64 * 1024;
    private const int FileChunkSize = 64 * 1024;

    public static async Task<(FrameKind Kind, byte[]? Payload)> ReceiveFrameAsync(
        NativeWebSocket webSocket,
        CancellationToken ct,
        long? maxBytes = null)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[BufferSize];
        WebSocketReceiveResult result;

        do
        {
            result = await webSocket.ReceiveAsync(buffer, ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                return (FrameKind.Close, null);

            ms.Write(buffer, 0, result.Count);

            if (maxBytes is { } limit && ms.Length > limit)
                return (FrameKind.TooLarge, null);
        } while (!result.EndOfMessage);

        var data = ms.ToArray();
        var kind = result.MessageType == WebSocketMessageType.Binary ? FrameKind.Binary : FrameKind.Text;
        return (kind, data);
    }

    public static async Task SendJsonAsync<T>(
        NativeWebSocket webSocket,
        T message,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        await webSocket.SendAsync(json, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public static async Task SendFileAsync(
        NativeWebSocket webSocket,
        string filePath,
        long length,
        CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileChunkSize, true);

        await SendStreamAsync(webSocket, fs, length, ct).ConfigureAwait(false);
    }

    public static async Task SendStreamAsync(
        NativeWebSocket webSocket,
        Stream stream,
        long length,
        CancellationToken ct)
    {
        var buffer = new byte[FileChunkSize];
        var remaining = length;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            remaining -= bytesRead;
            var endOfMessage = remaining <= 0;
            await webSocket.SendAsync(
                buffer.AsMemory(0, bytesRead),
                WebSocketMessageType.Binary,
                endOfMessage,
                ct).ConfigureAwait(false);
        }
    }

    public static async Task SendErrorAndCloseAsync(
        NativeWebSocket webSocket,
        string code,
        string message,
        int closeCode,
        ILogger logger,
        string sessionId,
        CancellationToken ct)
    {
        try
        {
            await SendJsonAsync(webSocket,
                new ErrorMessage(code, message, ErrorCatalog.CategoryOf(code)),
                AcdJsonContext.Default.ErrorMessage, ct);

            var status = closeCode switch
            {
                1008 => WebSocketCloseStatus.PolicyViolation,
                1011 => WebSocketCloseStatus.InternalServerError,
                1000 => WebSocketCloseStatus.NormalClosure,
                1001 => WebSocketCloseStatus.EndpointUnavailable,
                _ => WebSocketCloseStatus.InternalServerError
            };

            await CloseWebSocketAsync(webSocket, status, code, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{SessionId}] Error al enviar el frame de error", sessionId);
        }
    }

    public static async Task CloseWebSocketAsync(
        NativeWebSocket webSocket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken ct)
    {
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await webSocket.CloseAsync(status, description, ct).ConfigureAwait(false);
    }
}

public enum FrameKind
{
    Text,
    Binary,
    Close,
    TooLarge
}