using ACD.Configuration;
using ACD.Firma;
using ACD.Firma.Signing;
using ACD.PdfOpen;
using Microsoft.Extensions.Options;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

public sealed class AcdSessionHandlerFactory : IAcdSessionHandlerFactory
{
    private readonly IFirmaLauncher _firmaLauncher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AcdOptions _options;
    private readonly IPdfLauncher _pdfLauncher;
    private readonly PdfOpenStorage _pdfOpenStorage;

    public AcdSessionHandlerFactory(
        IOptions<AcdOptions> options,
        IFirmaLauncher firmaLauncher,
        IPdfLauncher pdfLauncher,
        PdfOpenStorage pdfOpenStorage,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _firmaLauncher = firmaLauncher;
        _pdfLauncher = pdfLauncher;
        _pdfOpenStorage = pdfOpenStorage;
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

        var pdfOpenHandler = new PdfOpenWorkflowHandler(
            _options.PdfOpen,
            _pdfOpenStorage,
            _pdfLauncher,
            logger,
            sessionId);

        return new AcdSessionHandler(firmaHandler, pdfOpenHandler, logger, sessionId, _options.WatchDirectory);
    }
}
