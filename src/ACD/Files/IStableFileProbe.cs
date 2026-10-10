namespace ACD.Files;

public interface IStableFileProbe
{
    Task<long> WaitUntilStableAsync(
        string path,
        StableFileProbeOptions? options = null,
        CancellationToken ct = default);
}
