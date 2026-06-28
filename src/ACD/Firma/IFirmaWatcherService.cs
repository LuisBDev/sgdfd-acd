using System.Threading.Channels;

namespace ACD.Firma;

public enum FirmaEventType
{
    FileReady,
    Timeout,
    Error
}

public record FirmaEvent(FirmaEventType Type, string FilePath, string? ErrorMessage = null);

public interface IFirmaWatcherService : IAsyncDisposable
{
    ChannelReader<FirmaEvent> Events { get; }
    void StartWatching(string originalFilename, string signedSuffix = "[F]");
}