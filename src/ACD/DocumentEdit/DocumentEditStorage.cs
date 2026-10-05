using System.Globalization;
using ACD.Configuration;

namespace ACD.DocumentEdit;

public sealed class DocumentEditStorage
{
    private readonly ILogger<DocumentEditStorage> _logger;
    private readonly DocumentEditOptions _options;
    private readonly TimeProvider _timeProvider;

    public DocumentEditStorage(DocumentEditOptions options, TimeProvider timeProvider, ILogger<DocumentEditStorage> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<DocumentEditWorkspace> SaveSourceAsync(
        DocumentEditRequest request,
        byte[] data,
        CancellationToken ct)
    {
        var directoryPath = BuildWorkspaceDirectory(request.RequestId);
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

    public void DeleteWorkspaceBestEffort(DocumentEditWorkspace workspace)
    {
        try
        {
            if (Directory.Exists(workspace.DirectoryPath)) Directory.Delete(workspace.DirectoryPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "No se pudo eliminar la carpeta de edición {Path}", workspace.DirectoryPath);
        }
    }

    private string BuildWorkspaceDirectory(Guid requestId)
    {
        var today = _timeProvider.GetLocalNow();
        return Path.Combine(
            _options.GetRootDirectory(),
            today.ToString("yyyy", CultureInfo.InvariantCulture),
            today.ToString("MM", CultureInfo.InvariantCulture),
            today.ToString("dd", CultureInfo.InvariantCulture),
            requestId.ToString("D"));
    }
}
