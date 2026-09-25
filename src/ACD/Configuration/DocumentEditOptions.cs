namespace ACD.Configuration;

public sealed class DocumentEditOptions
{
    private static readonly string[] DefaultAllowedExtensions = [".docx"];

    public string[] AllowedExtensions { get; init; } = [];
    public long MaxFileBytes { get; init; } = 20 * 1024 * 1024;
    public int RetentionHours { get; init; } = 24;
    public long MaxStorageBytes { get; init; } = 500 * 1024 * 1024;
    public int TimeoutMinutes { get; init; } = 60;
    public int PdfDebounceMilliseconds { get; init; } = 1500;
    public int PdfStabilizationTimeoutSeconds { get; init; } = 30;

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
