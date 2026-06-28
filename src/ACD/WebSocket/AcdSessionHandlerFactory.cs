using ACD.Configuration;
using ACD.Firma;
using Microsoft.Extensions.Options;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

/// <summary>
///     Fábrica singleton que compone AcdSessionHandler con sus dependencias por sesión.
///     Resuelve servicios scoped (IFileDepositService, IFirmaWatcherService) desde el scope inyectado.
/// </summary>
public sealed class AcdSessionHandlerFactory : IAcdSessionHandlerFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly AcdOptions _options;

    public AcdSessionHandlerFactory(IOptions<AcdOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public AcdSessionHandler Create(string sessionId, NativeWebSocket webSocket, IServiceScope scope)
    {
        var depositService = scope.ServiceProvider.GetRequiredService<IFileDepositService>();
        var watcherService = scope.ServiceProvider.GetRequiredService<IFirmaWatcherService>();
        var logger = _loggerFactory.CreateLogger<AcdSessionHandler>();

        var firmaHandler = new FirmaWorkflowHandler(
            depositService,
            watcherService,
            _options.WatchDirectory,
            _options.FirmaTimeoutSeconds,
            _options.FirmaSignedSuffix,
            logger,
            sessionId);

        return new AcdSessionHandler(firmaHandler, logger, sessionId, _options.WatchDirectory);
    }
}