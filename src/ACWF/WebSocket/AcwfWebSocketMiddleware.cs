using ACWF.Configuration;
using ACWF.Firma;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Alias to avoid ACWF.System shadowing System.Net.WebSockets
using NativeWebSocket = global::System.Net.WebSockets.WebSocket;
using WebSocketCloseStatus = global::System.Net.WebSockets.WebSocketCloseStatus;

namespace ACWF.WebSocket;

/// <summary>
/// ASP.NET Core middleware that handles WebSocket upgrades on /acwf.
/// Enforces Origin validation, single-session gate, and delegates to AcwfSessionHandler.
/// </summary>
public sealed class AcwfWebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISessionGate _sessionGate;
    private readonly AcwfOptions _options;
    private readonly ILogger<AcwfWebSocketMiddleware> _logger;
    private readonly IServiceProvider _sp;

    public AcwfWebSocketMiddleware(
        RequestDelegate next,
        ISessionGate sessionGate,
        IOptions<AcwfOptions> options,
        ILogger<AcwfWebSocketMiddleware> logger,
        IServiceProvider sp)
    {
        _next = next;
        _sessionGate = sessionGate;
        _options = options.Value;
        _logger = logger;
        _sp = sp;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only handle requests to the /acwf endpoint.
        if (!context.Request.Path.Equals("/acwf", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Must be a WebSocket upgrade request.
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Validate Origin header.
        if (!IsOriginAllowed(context.Request.Headers.Origin.ToString()))
        {
            _logger.LogWarning(
                "WebSocket upgrade rejected — Origin '{Origin}' not in AllowedOrigins",
                context.Request.Headers.Origin.ToString());
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Attempt to acquire the single-session gate.
        bool acquired = await _sessionGate.TryAcquireAsync(context.RequestAborted);

        if (!acquired)
        {
            _logger.LogWarning("WebSocket upgrade rejected — session already active (4002)");
            // Must accept the upgrade before sending the close frame.
            var busyWs = await context.WebSockets.AcceptWebSocketAsync();
            await busyWs.CloseAsync(
                (WebSocketCloseStatus)4002,
                "Session already active",
                context.RequestAborted);
            return;
        }

        // Create a DI scope for this session (scoped services: FileDepositService, FirmaWatcherService).
        await using var scope = _sp.CreateAsyncScope();
        var depositService = scope.ServiceProvider.GetRequiredService<IFileDepositService>();
        var watcherService = scope.ServiceProvider.GetRequiredService<IFirmaWatcherService>();

        string sessionId = Guid.NewGuid().ToString("N");
        NativeWebSocket? webSocket = null;

        try
        {
            webSocket = await context.WebSockets.AcceptWebSocketAsync();
            _logger.LogInformation("WebSocket session opened: {SessionId}", sessionId);

            var handler = new AcwfSessionHandler(depositService, watcherService, _logger, sessionId, _options.WatchDirectory, _options.FirmaTimeoutSeconds);
            await handler.HandleAsync(webSocket, context.RequestAborted);
        }
        finally
        {
            _sessionGate.Release();
            await watcherService.DisposeAsync();

            if (webSocket is not null)
            {
                _logger.LogInformation("WebSocket session closed: {SessionId}", sessionId);
            }
        }
    }

    private bool IsOriginAllowed(string origin)
    {
        if (_options.AllowedOrigins.Length == 0) return false;

        foreach (var allowed in _options.AllowedOrigins)
        {
            if (string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
