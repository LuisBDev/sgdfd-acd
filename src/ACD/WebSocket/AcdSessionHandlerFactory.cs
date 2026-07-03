using ACD.Configuration;
using ACD.Firma;
using ACD.Firma.Signing;
using Microsoft.Extensions.Options;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

public sealed class AcdSessionHandlerFactory : IAcdSessionHandlerFactory
{
    private readonly IFirmaLauncher _firmaLauncher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AcdOptions _options;

    public AcdSessionHandlerFactory(
        IOptions<AcdOptions> options,
        IFirmaLauncher firmaLauncher,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _firmaLauncher = firmaLauncher;
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
            _firmaLauncher,
            _options.Firma,
            _options.WatchDirectory,
            _options.FirmaTimeoutSeconds,
            logger,
            sessionId);

        return new AcdSessionHandler(firmaHandler, logger, sessionId, _options.WatchDirectory);
    }
}