using ACD.Configuration;
using ACD.DocumentEdit;
using ACD.Files;
using ACD.Firma;
using ACD.Firma.Signing;
using ACD.PdfOpen;
using Microsoft.Extensions.Options;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

public sealed class AcdSessionHandlerFactory : IAcdSessionHandlerFactory
{
    private readonly DocumentEditStorage _documentEditStorage;
    private readonly IEditedPdfWatcherFactory _editedPdfWatcherFactory;
    private readonly IFirmaLauncher _firmaLauncher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AcdOptions _options;
    private readonly IShellLauncher _shellLauncher;
    private readonly PdfOpenStorage _pdfOpenStorage;
    private readonly ISessionGate _sessionGate;

    public AcdSessionHandlerFactory(
        IOptions<AcdOptions> options,
        IFirmaLauncher firmaLauncher,
        IShellLauncher shellLauncher,
        PdfOpenStorage pdfOpenStorage,
        DocumentEditStorage documentEditStorage,
        IEditedPdfWatcherFactory editedPdfWatcherFactory,
        ISessionGate sessionGate,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _firmaLauncher = firmaLauncher;
        _shellLauncher = shellLauncher;
        _pdfOpenStorage = pdfOpenStorage;
        _documentEditStorage = documentEditStorage;
        _editedPdfWatcherFactory = editedPdfWatcherFactory;
        _sessionGate = sessionGate;
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
            _shellLauncher,
            logger,
            sessionId);

        var documentEditHandler = new DocumentEditWorkflowHandler(
            _options.DocumentEdit,
            _documentEditStorage,
            _editedPdfWatcherFactory,
            _shellLauncher,
            logger,
            sessionId);

        return new AcdSessionHandler(firmaHandler, pdfOpenHandler, documentEditHandler, _sessionGate, logger, sessionId, _options.WatchDirectory);
    }
}
