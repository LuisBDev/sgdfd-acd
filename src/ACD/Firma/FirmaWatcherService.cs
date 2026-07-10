using System.Threading.Channels;
using ACD.Configuration;
using ACD.Firma.Signing;
using Microsoft.Extensions.Options;

namespace ACD.Firma;

public sealed class FirmaWatcherService : IFirmaWatcherService
{
    private const int PollIntervalMs = 500;
    private const string ArchiveDirectoryName = "firmados";

    private readonly Channel<FirmaEvent> _channel = Channel.CreateUnbounded<FirmaEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ILogger<FirmaWatcherService> _logger;
    private readonly AcdOptions _options;
    private string? _expectedFilename;
    private CancellationTokenSource? _timeoutCts;
    private int _waitStarted;

    private FileSystemWatcher? _watcher;

    public FirmaWatcherService(
        IOptions<AcdOptions> options,
        ILogger<FirmaWatcherService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public ChannelReader<FirmaEvent> Events => _channel.Reader;

    public void StartWatching(string originalFilename, string? tipo, bool numera)
    {
        var signedSuffix = FirmaTipo.SignedSuffix(tipo, numera);
        _expectedFilename = FirmaTipo.SignedFileName(originalFilename, tipo, numera);
        _waitStarted = 0;
        _logger.LogInformation("FirmaWatcher iniciado. Esperando archivo: {ExpectedFile}", _expectedFilename);

        _watcher = new FileSystemWatcher(_options.WatchDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            Filter = $"*{signedSuffix}.pdf",
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;

        _timeoutCts = new CancellationTokenSource();
        var timeoutToken = _timeoutCts.Token;

        _ = Task.Delay(TimeSpan.FromSeconds(_options.FirmaTimeoutSeconds), timeoutToken)
            .ContinueWith(
                t => OnTimeout(t, originalFilename),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _timeoutCts = null;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _channel.Writer.TryComplete();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // Barre PDF firmados residuales de la raíz y los archiva en firmados\{yyyy}\{MM}.
    // Excluye el firmado en vuelo para no archivarlo antes de enviarlo.
    public void ArchiveSignedResiduals(string expectedSignedFilename)
    {
        IEnumerable<string> pdfs;
        try
        {
            pdfs = Directory.EnumerateFiles(_options.WatchDirectory, "*.pdf", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo enumerar TFIRMA para archivar firmados residuales");
            return;
        }

        foreach (var stalePath in pdfs)
        {
            var staleFilename = Path.GetFileName(stalePath);

            // No archivar el firmado que estamos esperando en este flujo.
            if (string.Equals(staleFilename, expectedSignedFilename, StringComparison.OrdinalIgnoreCase))
                continue;

            var nameNoExt = Path.GetFileNameWithoutExtension(staleFilename);
            if (!HasSignedSuffix(nameNoExt)) continue;

            try
            {
                // La última modificación del PDF es la fecha de firma; gobierna la
                // carpeta {yyyy}\{MM}. El nombre  es único por su timestamp de descarga.
                var signedAt = File.GetLastWriteTime(stalePath);
                var archiveDirectory = Path.Combine(
                    _options.WatchDirectory, ArchiveDirectoryName,
                    signedAt.ToString("yyyy"), signedAt.ToString("MM"));
                Directory.CreateDirectory(archiveDirectory);

                var archivedPath = BuildUniqueArchivePath(archiveDirectory, $"{nameNoExt}_archived");

                File.Move(stalePath, archivedPath);
                _logger.LogInformation(
                    "Firmado residual archivado: {StaleFile} -> {ArchivedPath}",
                    staleFilename, archivedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // El archivado es best-effort: la firma continúa aunque el move falle.
                _logger.LogWarning(ex, "No se pudo archivar el firmado residual, se continúa: {StalePath}", stalePath);
            }
        }
    }

    // Elimina PDF originales residuales (no firmados) de la raíz.
    // Los firmados se gestionan en ArchiveSignedResiduals. Excluye el original in-flight.
    public void CleanupStaleOriginals(string activeOriginalFilename)
    {
        IEnumerable<string> pdfs;
        try
        {
            pdfs = Directory.EnumerateFiles(_options.WatchDirectory, "*.pdf", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo enumerar TFIRMA para limpiar originales residuales");
            return;
        }

        foreach (var path in pdfs)
        {
            var filename = Path.GetFileName(path);

            // No tocar el original que FirmaONPE está firmando ahora.
            if (string.Equals(filename, activeOriginalFilename, StringComparison.OrdinalIgnoreCase))
                continue;

            // Solo originales: los firmados los gestiona ArchiveSignedResiduals.
            if (HasSignedSuffix(Path.GetFileNameWithoutExtension(filename))) continue;

            try
            {
                File.Delete(path);
                _logger.LogInformation("Original residual eliminado: {File}", filename);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "No se pudo eliminar el original residual, se continúa: {Path}", path);
            }
        }
    }

    private static bool HasSignedSuffix(string nameWithoutExtension)
    {
        return FirmaTipo.SignedSuffixes.Any(suffix =>
            nameWithoutExtension.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildUniqueArchivePath(string directory, string baseFilename)
    {
        var path = Path.Combine(directory, baseFilename + ".pdf");
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(directory, $"{baseFilename}_{i}.pdf");
        return path;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (_expectedFilename is null) return;

        if (!string.Equals(Path.GetFileName(e.FullPath), _expectedFilename, StringComparison.OrdinalIgnoreCase))
            return;

        // El archivo aparece vacío y bloqueado; arrancar la espera una sola vez.
        if (Interlocked.Exchange(ref _waitStarted, 1) != 0) return;

        _logger.LogInformation("Archivo de firma detectado, esperando a que se complete: {FilePath}", e.FullPath);
        _ = WaitForSignedFileAsync(e.FullPath, _timeoutCts?.Token ?? CancellationToken.None);
    }

    // Espera a que el archivo esté desbloqueado y con tamaño estable (>0) entre dos lecturas.
    private async Task WaitForSignedFileAsync(string path, CancellationToken token)
    {
        var previousLength = -1L;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var readable = TryGetReadableLength(path, out var length);

                if (readable && length == previousLength)
                {
                    _timeoutCts?.Cancel();
                    _logger.LogInformation("Archivo de firma listo: {FilePath} ({Bytes} bytes)", path, length);
                    await _channel.Writer.WriteAsync(new FirmaEvent(FirmaEventType.FileReady, path)).ConfigureAwait(false);
                    return;
                }

                previousLength = readable ? length : -1;
                await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout global o cierre de sesión — OnTimeout emite el evento Timeout.
        }
    }

    private static bool TryGetReadableLength(string path, out long length)
    {
        length = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    private void OnTimeout(Task completedTask, string originalFilename)
    {
        if (completedTask.IsCanceled)
            return;

        _logger.LogWarning(
            "FirmaWatcher agotó el tiempo de espera tras {Seconds}s para {Filename}",
            _options.FirmaTimeoutSeconds, originalFilename);

        _channel.Writer.TryWrite(new FirmaEvent(FirmaEventType.Timeout, string.Empty));
    }
}