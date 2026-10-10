namespace ACD.Configuration;

public sealed class DocumentWorkspaceOptions
{
    private const int DefaultConversionTimeoutSeconds = 90;

    public string? RootDirectory { get; init; }
    public long MaxFileBytes { get; init; } = 20 * 1024 * 1024;
    public int ConversionTimeoutSeconds { get; init; } = DefaultConversionTimeoutSeconds;
    public int WatchDebounceMilliseconds { get; init; } = 1000;

    public TimeSpan GetConversionTimeout() =>
        TimeSpan.FromSeconds(ConversionTimeoutSeconds > 0 ? ConversionTimeoutSeconds : DefaultConversionTimeoutSeconds);

    public TimeSpan GetWatchDebounce() =>
        TimeSpan.FromMilliseconds(Math.Max(0, WatchDebounceMilliseconds));

    public string GetRootDirectory() =>
        string.IsNullOrWhiteSpace(RootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TDOCUMENTOS", "MPD")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(RootDirectory.Trim()));
}
