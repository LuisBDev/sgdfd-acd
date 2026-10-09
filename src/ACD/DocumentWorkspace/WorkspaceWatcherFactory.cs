using ACD.Configuration;
using ACD.Files;
using Microsoft.Extensions.Options;

namespace ACD.DocumentWorkspace;

public sealed class WorkspaceWatcherFactory(
    IOptions<AcdOptions> options,
    WorkspaceFileStore store,
    IStableFileProbe probe,
    ILoggerFactory loggerFactory) : IWorkspaceWatcherFactory
{
    private readonly TimeSpan _debounce = options.Value.DocumentWorkspace.GetWatchDebounce();

    public IWorkspaceWatcher Create(string directory) =>
        new WorkspaceWatcher(directory, store, probe, _debounce, loggerFactory.CreateLogger<WorkspaceWatcher>());
}
