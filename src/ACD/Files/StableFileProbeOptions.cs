namespace ACD.Files;

public sealed record StableFileProbeOptions
{
    public static StableFileProbeOptions Default { get; } = new();

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public int RequiredStableReads { get; init; } = 2;
    public FileShare OpenShare { get; init; } = FileShare.Read;
}
