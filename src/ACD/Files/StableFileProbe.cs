namespace ACD.Files;

public sealed class StableFileProbe : IStableFileProbe
{
    public async Task<long> WaitUntilStableAsync(
        string path,
        StableFileProbeOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= StableFileProbeOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RequiredStableReads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PollInterval, TimeSpan.Zero);

        var previousLength = -1L;
        var stableReads = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (TryGetReadableLength(path, options.OpenShare, out var length))
            {
                stableReads = length == previousLength ? stableReads + 1 : 1;
                previousLength = length;
                if (stableReads >= options.RequiredStableReads) return length;
            }
            else
            {
                previousLength = -1;
                stableReads = 0;
            }

            await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
        }
    }

    private static bool TryGetReadableLength(string path, FileShare share, out long length)
    {
        length = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            length = fs.Length;
            return length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
