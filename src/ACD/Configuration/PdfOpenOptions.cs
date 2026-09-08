namespace ACD.Configuration;

public sealed class PdfOpenOptions
{
    public long MaxFileSizeBytes { get; init; } = 50 * 1024 * 1024;
    public int RetentionHours { get; init; } = 8;
    public long MaxStorageBytes { get; init; } = 500 * 1024 * 1024;
}
