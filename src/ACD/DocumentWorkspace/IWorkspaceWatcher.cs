using System.Threading.Channels;

namespace ACD.DocumentWorkspace;

public interface IWorkspaceWatcher : IAsyncDisposable
{
    WordFileInfo? Current { get; }
    ChannelReader<WordFileInfo?> Changes { get; }
    void Start(CancellationToken ct);
}
