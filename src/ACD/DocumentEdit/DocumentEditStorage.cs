using ACD.Configuration;

namespace ACD.DocumentEdit;

public sealed class DocumentEditStorage
{
    private readonly ILogger<DocumentEditStorage> _logger;
    private readonly DocumentEditOptions _options;
    private readonly string _rootDirectory;

    public DocumentEditStorage(DocumentEditOptions options, ILogger<DocumentEditStorage> logger)
    {
        _options = options;
        _logger = logger;
        _rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ACD",
            "Temp",
            "DocumentEdit");
    }

    public string GetWorkspaceDirectory(Guid requestId) =>
        Path.Combine(_rootDirectory, requestId.ToString("D"));

    public async Task<DocumentEditWorkspace> SaveSourceAsync(
        DocumentEditRequest request,
        byte[] data,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_rootDirectory);
        CleanupExpiredBestEffort();

        var currentSize = Directory.EnumerateFiles(_rootDirectory, "*", SearchOption.AllDirectories)
            .Sum(TryGetLength);
        if (currentSize + data.LongLength > _options.MaxStorageBytes)
            throw new DocumentEditStorageLimitException();

        var directoryPath = GetWorkspaceDirectory(request.RequestId);
        Directory.CreateDirectory(directoryPath);

        var sourcePath = Path.Combine(directoryPath, request.SafeFilename);
        await using (var stream = new FileStream(
                         sourcePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
        }

        return new DocumentEditWorkspace(request.RequestId, directoryPath, sourcePath);
    }

    public void DeleteWorkspaceBestEffort(Guid requestId)
    {
        var directoryPath = GetWorkspaceDirectory(requestId);
        try
        {
            if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "No se pudo eliminar la carpeta de edición {Path}", directoryPath);
        }
    }

    public void CleanupExpiredBestEffort()
    {
        if (!Directory.Exists(_rootDirectory)) return;

        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, _options.RetentionHours));
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_rootDirectory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "No se pudo listar las carpetas de edición en {Path}", _rootDirectory);
            return;
        }

        foreach (var directoryPath in directories)
        {
            try
            {
                if (GetLastActivityUtc(directoryPath) < cutoff) Directory.Delete(directoryPath, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "No se pudo eliminar la carpeta de edición expirada {Path}", directoryPath);
            }
        }
    }

    private static DateTime GetLastActivityUtc(string directoryPath)
    {
        var lastActivity = Directory.GetLastWriteTimeUtc(directoryPath);
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            var fileWrite = File.GetLastWriteTimeUtc(filePath);
            if (fileWrite > lastActivity) lastActivity = fileWrite;
        }

        return lastActivity;
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

public sealed class DocumentEditStorageLimitException : Exception;
