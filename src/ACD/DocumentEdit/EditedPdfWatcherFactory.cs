using ACD.Configuration;
using ACD.Files;
using Microsoft.Extensions.Options;

namespace ACD.DocumentEdit;

public sealed class EditedPdfWatcherFactory : IEditedPdfWatcherFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly DocumentEditOptions _options;
    private readonly IStableFileProbe _stableFileProbe;

    public EditedPdfWatcherFactory(
        IOptions<AcdOptions> options,
        IStableFileProbe stableFileProbe,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value.DocumentEdit;
        _stableFileProbe = stableFileProbe;
        _loggerFactory = loggerFactory;
    }

    public IEditedPdfWatcher Create(DocumentEditWorkspace workspace) =>
        new EditedPdfWatcher(
            workspace,
            _options,
            _stableFileProbe,
            _loggerFactory.CreateLogger<EditedPdfWatcher>());
}
