using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

public interface IAcdSessionHandlerFactory
{
    AcdSessionHandler Create(string sessionId, NativeWebSocket webSocket, IServiceScope scope);
}