using System.Threading.Channels;

namespace ACD.DocumentEdit;

public sealed record EditedPdfVersion(string FilePath, string FileName, long Size, int Version);

public interface IEditedPdfWatcher : IAsyncDisposable, IDisposable
{
    ChannelReader<EditedPdfVersion> Versions { get; }

    EditedPdfVersion? LatestVersion { get; }

    void Start(CancellationToken ct = default);
}
