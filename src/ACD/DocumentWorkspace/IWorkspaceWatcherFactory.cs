namespace ACD.DocumentWorkspace;

public interface IWorkspaceWatcherFactory
{
    IWorkspaceWatcher Create(string directory);
}
