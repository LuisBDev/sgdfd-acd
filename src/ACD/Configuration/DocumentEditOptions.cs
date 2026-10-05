namespace ACD.Configuration;

public sealed class DocumentEditOptions
{
    private static readonly string[] DefaultAllowedExtensions = [".docx"];

    public string RootDirectory { get; init; } = string.Empty;
    public string[] AllowedExtensions { get; init; } = [];
    public long MaxFileBytes { get; init; } = 20 * 1024 * 1024;
    public int TimeoutMinutes { get; init; } = 60;
    public int PdfDebounceMilliseconds { get; init; } = 1500;
    public int PdfStabilizationTimeoutSeconds { get; init; } = 30;

    public string GetRootDirectory() =>
        string.IsNullOrWhiteSpace(RootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TDOCUMENTOS", "MPD")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(RootDirectory.Trim()));

    public IReadOnlySet<string> GetAllowedExtensions()
    {
        var extensions = AllowedExtensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return extensions.Count > 0
            ? extensions
            : DefaultAllowedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
