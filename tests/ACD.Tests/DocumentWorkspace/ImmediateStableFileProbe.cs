using ACD.Files;

namespace ACD.Tests.DocumentWorkspace;

public sealed class ImmediateStableFileProbe : IStableFileProbe
{
    public Task<long> WaitUntilStableAsync(
        string path,
        StableFileProbeOptions? options = null,
        CancellationToken ct = default) =>
        Task.FromResult(0L);
}
