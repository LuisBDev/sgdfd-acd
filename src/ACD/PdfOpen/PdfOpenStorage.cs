using ACD.Configuration;

namespace ACD.PdfOpen;

public sealed class PdfOpenStorage
{
    private readonly ILogger<PdfOpenStorage> _logger;
    private readonly PdfOpenOptions _options;
    private readonly string _rootDirectory;

    public PdfOpenStorage(PdfOpenOptions options, ILogger<PdfOpenStorage> logger)
    {
        _options = options;
        _logger = logger;
        _rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ACD",
            "Temp",
            "PdfOpen");
    }

    public async Task<string> SaveAsync(PdfOpenRequest request, byte[] data, CancellationToken ct)
    {
        Directory.CreateDirectory(_rootDirectory);
        CleanupBestEffort();

        var currentSize = Directory.EnumerateFiles(_rootDirectory, "*.pdf")
            .Sum(path => TryGetLength(path));
        if (currentSize + data.LongLength > _options.MaxStorageBytes)
            throw new PdfOpenStorageLimitException();

        var displayName = Path.GetFileNameWithoutExtension(request.SafeFilename);
        if (displayName.Length > 80) displayName = displayName[..80];

        var filePath = Path.Combine(_rootDirectory, $"{request.RequestId:N}-{displayName}.pdf");
        await using var stream = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        return filePath;
    }

    private void CleanupBestEffort()
    {
        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, _options.RetentionHours));
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "*.pdf"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "No se pudo eliminar el PDF temporal expirado {Path}", path);
            }
        }
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}

public sealed class PdfOpenStorageLimitException : Exception;
