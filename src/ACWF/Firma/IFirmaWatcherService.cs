using System.Threading.Channels;

namespace ACWF.Firma;

public enum FirmaEventType
{
    FileReady,
    Timeout,
    Error
}

public record FirmaEvent(FirmaEventType Type, string FilePath, string? ErrorMessage = null);

public interface IFirmaWatcherService : IAsyncDisposable
{
    void StartWatching(string originalFilename);
    ChannelReader<FirmaEvent> Events { get; }
}
