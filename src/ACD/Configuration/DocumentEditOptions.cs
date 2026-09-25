namespace ACD.Configuration;

public sealed class DocumentEditOptions
{
    public string[] AllowedExtensions { get; init; } = [".docx"];
    public long MaxFileBytes { get; init; } = 20 * 1024 * 1024;
    public int RetentionHours { get; init; } = 24;
    public long MaxStorageBytes { get; init; } = 500 * 1024 * 1024;
    public int TimeoutMinutes { get; init; } = 60;
}
