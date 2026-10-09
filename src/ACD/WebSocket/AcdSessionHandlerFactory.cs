using ACD.Configuration;
using ACD.DocumentWorkspace;
using ACD.Files;
using ACD.Firma;
using ACD.Firma.Signing;
using ACD.PdfOpen;
using Microsoft.Extensions.Options;
using NativeWebSocket = System.Net.WebSockets.WebSocket;

namespace ACD.WebSocket;

public sealed class AcdSessionHandlerFactory : IAcdSessionHandlerFactory
{
    private readonly IConversionService _conversionService;
    private readonly IFirmaLauncher _firmaLauncher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AcdOptions _options;
    private readonly IShellLauncher _shellLauncher;
    private readonly PdfOpenStorage _pdfOpenStorage;
    private readonly ISessionGate _sessionGate;
    private readonly WorkspaceFileStore _workspaceFileStore;
    private readonly IWorkspaceWatcherFactory _workspaceWatcherFactory;
    private readonly WorkspacePaths _workspacePaths;

    public AcdSessionHandlerFactory(
        IOptions<AcdOptions> options,
        IFirmaLauncher firmaLauncher,
        IShellLauncher shellLauncher,
        PdfOpenStorage pdfOpenStorage,
        WorkspacePaths workspacePaths,
        WorkspaceFileStore workspaceFileStore,
        IConversionService conversionService,
        IWorkspaceWatcherFactory workspaceWatcherFactory,
        ISessionGate sessionGate,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _firmaLauncher = firmaLauncher;
        _shellLauncher = shellLauncher;
        _pdfOpenStorage = pdfOpenStorage;
        _workspacePaths = workspacePaths;
        _workspaceFileStore = workspaceFileStore;
        _conversionService = conversionService;
        _workspaceWatcherFactory = workspaceWatcherFactory;
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

        var documentWorkspaceHandler = new DocumentWorkspaceHandler(
            _options.DocumentWorkspace,
            _workspacePaths,
            _workspaceFileStore,
            _shellLauncher,
            _conversionService,
            logger,
            sessionId);

        var workspaceWatchSession = new WorkspaceWatchSession(_workspaceWatcherFactory, _workspacePaths, logger, sessionId);

        return new AcdSessionHandler(firmaHandler, pdfOpenHandler, documentWorkspaceHandler, workspaceWatchSession, _sessionGate, logger, sessionId, _options.WatchDirectory);
    }
}
