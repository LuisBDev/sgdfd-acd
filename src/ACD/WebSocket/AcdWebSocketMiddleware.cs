using ACD.Configuration;
using ACD.Firma;
using Microsoft.Extensions.Options;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

/// <summary>
///     Middleware de ASP.NET Core que maneja WebSocket upgrades en /acd.
///     Aplica validación de Origin y delega a AcdSessionHandler. La exclusividad
///     se controla por operación dentro de la sesión, después de autenticarla.
/// </summary>
public sealed class AcdWebSocketMiddleware
{
    private readonly IAcdSessionHandlerFactory _factory;
    private readonly ILogger<AcdWebSocketMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly AcdOptions _options;
    private readonly ISessionGate _sessionGate;
    private readonly IServiceProvider _sp;

    public AcdWebSocketMiddleware(
        RequestDelegate next,
        ISessionGate sessionGate,
        IOptions<AcdOptions> options,
        ILogger<AcdWebSocketMiddleware> logger,
        IAcdSessionHandlerFactory factory,
        IServiceProvider sp)
    {
        _next = next;
        _sessionGate = sessionGate;
        _options = options.Value;
        _logger = logger;
        _factory = factory;
        _sp = sp;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Solo manejar requests al endpoint /acd.
        if (!context.Request.Path.Equals("/acd", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Solicitud no-WebSocket en /acd → health check (sin session gate).
        if (!context.WebSockets.IsWebSocketRequest)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (IsOriginAllowed(origin)) context.Response.Headers["Access-Control-Allow-Origin"] = origin;

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"status\":\"ok\"}", context.RequestAborted);
            return;
        }

        // Validar header Origin.
        if (!IsOriginAllowed(context.Request.Headers.Origin.ToString()))
        {
            _logger.LogWarning(
                "Upgrade WebSocket rechazado — el Origin '{Origin}' no está en AllowedOrigins",
                context.Request.Headers.Origin.ToString());
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!await _sessionGate.TryAcquireConnectionAsync(context.RequestAborted))
        {
            _logger.LogWarning("Upgrade WebSocket rechazado — capacidad de dos conexiones alcanzada (4002)");
            var busyWs = await context.WebSockets.AcceptWebSocketAsync();
            await busyWs.CloseAsync(
                (System.Net.WebSockets.WebSocketCloseStatus)4002,
                "Connection capacity reached",
                context.RequestAborted);
            return;
        }

        try
        {
            // Crear un DI scope para esta sesión (scoped services: FileDepositService, FirmaWatcherService).
            await using var scope = _sp.CreateAsyncScope();
            var watcherService = scope.ServiceProvider.GetRequiredService<IFirmaWatcherService>();

            var sessionId = Guid.NewGuid().ToString("N");
            NativeWebSocket? webSocket = null;

            try
            {
                webSocket = await context.WebSockets.AcceptWebSocketAsync();
                _logger.LogInformation("Sesión WebSocket abierta: {SessionId}", sessionId);

                var handler = _factory.Create(sessionId, webSocket, scope);
                await handler.HandleAsync(webSocket, context.RequestAborted);
            }
            finally
            {
                await watcherService.DisposeAsync();

                if (webSocket is not null) _logger.LogInformation("Sesión WebSocket cerrada: {SessionId}", sessionId);
            }
        }
        finally
        {
            _sessionGate.ReleaseConnection();
        }
    }

    private bool IsOriginAllowed(string origin)
    {
        if (_options.AllowedOrigins.Length == 0) return false;
        if (_options.AllowedOrigins.Contains("*")) return true;

        foreach (var allowed in _options.AllowedOrigins)
            if (string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
