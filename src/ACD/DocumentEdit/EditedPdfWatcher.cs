using System.Security.Cryptography;
using System.Threading.Channels;
using ACD.Configuration;
using ACD.Files;

namespace ACD.DocumentEdit;

public sealed class EditedPdfWatcher : IEditedPdfWatcher
{
    private const string PdfExtension = ".pdf";
    private const int TrailerWindowBytes = 1024;

    private static readonly byte[] PdfHeader = "%PDF-"u8.ToArray();
    private static readonly byte[] PdfTrailer = "%%EOF"u8.ToArray();

    private static readonly StableFileProbeOptions ProbeOptions = new()
    {
        OpenShare = FileShare.ReadWrite | FileShare.Delete
    };

    private readonly Channel<EditedPdfVersion> _channel = Channel.CreateUnbounded<EditedPdfVersion>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly ILogger<EditedPdfWatcher> _logger;
    private readonly Dictionary<string, PdfFileState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _stabilizationTimeout;
    private readonly IStableFileProbe _stableFileProbe;
    private readonly DocumentEditWorkspace _workspace;

    private CancellationTokenRegistration _externalRegistration;
    private EditedPdfVersion? _latestVersion;
    private int _state;
    private int _versionCount;
    private FileSystemWatcher? _watcher;

    public EditedPdfWatcher(
        DocumentEditWorkspace workspace,
        DocumentEditOptions options,
        IStableFileProbe stableFileProbe,
        ILogger<EditedPdfWatcher> logger)
    {
        _workspace = workspace;
        _stableFileProbe = stableFileProbe;
        _logger = logger;
        _debounce = TimeSpan.FromMilliseconds(Math.Max(0, options.PdfDebounceMilliseconds));
        _stabilizationTimeout = TimeSpan.FromSeconds(Math.Max(1, options.PdfStabilizationTimeoutSeconds));
    }

    public ChannelReader<EditedPdfVersion> Versions => _channel.Reader;

    public EditedPdfVersion? LatestVersion
    {
        get
        {
            lock (_gate) return _latestVersion;
        }
    }

    public void Start(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == 2, this);
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("EditedPdfWatcher ya fue iniciado");

        var watcher = new FileSystemWatcher(_workspace.DirectoryPath)
        {
            Filter = "*" + PdfExtension,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024
        };
        watcher.Created += OnFileEvent;
        watcher.Changed += OnFileEvent;
        watcher.Renamed += OnFileEvent;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;

        if (ct.CanBeCanceled)
            _externalRegistration = ct.Register(static state => ((EditedPdfWatcher)state!).CancelBestEffort(), this);

        _logger.LogInformation(
            "EditedPdfWatcher iniciado para el pedido {RequestId} en {Directory}",
            _workspace.RequestId, _workspace.DirectoryPath);

        _ = Task.Run(ScanExistingFiles, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryShutdown(out var pending)) return;

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tareas del EditedPdfWatcher finalizadas con error durante el cierre");
        }

        _cts.Dispose();
    }

    public void Dispose() => TryShutdown(out _);

    private bool TryShutdown(out Task[] pending)
    {
        pending = [];
        if (Interlocked.Exchange(ref _state, 2) == 2) return false;

        CancelBestEffort();
        _externalRegistration.Dispose();

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileEvent;
            _watcher.Changed -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        _channel.Writer.TryComplete();

        lock (_gate)
        {
            pending = _states.Values
                .Select(state => state.Worker)
                .OfType<Task>()
                .Where(task => !task.IsCompleted)
                .ToArray();
        }

        return true;
    }

    private void CancelBestEffort()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            Schedule(e.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo programar la revisión del PDF {Path}", e.FullPath);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "Error del vigilante de PDF en {Directory}; se reescanea la carpeta", _workspace.DirectoryPath);
        _ = Task.Run(ScanExistingFiles, CancellationToken.None);
    }

    private void ScanExistingFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_workspace.DirectoryPath, "*" + PdfExtension, SearchOption.TopDirectoryOnly))
                Schedule(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "No se pudo escanear la carpeta de edición {Directory}", _workspace.DirectoryPath);
        }
    }

    private void Schedule(string path)
    {
        if (_cts.IsCancellationRequested || !IsCandidate(path)) return;

        lock (_gate)
        {
            if (Volatile.Read(ref _state) != 1) return;

            if (!_states.TryGetValue(path, out var state))
            {
                state = new PdfFileState();
                _states[path] = state;
            }

            state.LastEventAt = Environment.TickCount64;
            state.Generation++;

            if (state.Running) return;
            state.Running = true;
            state.Worker = Task.Run(() => ProcessAsync(path, state), CancellationToken.None);
        }
    }

    private bool IsCandidate(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Length > PdfExtension.Length
               && fileName.EndsWith(PdfExtension, StringComparison.OrdinalIgnoreCase)
               && !fileName.StartsWith('~')
               && !string.Equals(path, _workspace.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ProcessAsync(string path, PdfFileState state)
    {
        var token = _cts.Token;
        try
        {
            while (true)
            {
                long generation;
                while (!TryGetQuietGeneration(state, out generation, out var remaining))
                    await Task.Delay(remaining, token).ConfigureAwait(false);

                await TryEmitVersionAsync(path, state, token).ConfigureAwait(false);

                lock (_gate)
                {
                    if (state.Generation != generation) continue;
                    state.Running = false;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error inesperado al revisar el PDF {Path}; se reintentará con el siguiente evento", path);
            lock (_gate) state.Running = false;
        }
    }

    private bool TryGetQuietGeneration(PdfFileState state, out long generation, out TimeSpan remaining)
    {
        lock (_gate)
        {
            generation = state.Generation;
            var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - state.LastEventAt);
            remaining = _debounce - elapsed;
            return remaining <= TimeSpan.Zero;
        }
    }

    private async Task TryEmitVersionAsync(string path, PdfFileState state, CancellationToken token)
    {
        if (!File.Exists(path)) return;

        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeoutCts.CancelAfter(_stabilizationTimeout);
            try
            {
                await _stableFileProbe.WaitUntilStableAsync(path, ProbeOptions, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _logger.LogDebug("El PDF {Path} no se estabilizó a tiempo; se espera el siguiente evento", path);
                return;
            }
        }

        if (!TryReadPdf(path, out var size, out var sha256))
        {
            _logger.LogDebug("El PDF {Path} aún no es válido; se espera el siguiente evento", path);
            return;
        }

        EditedPdfVersion version;
        lock (_gate)
        {
            if (Volatile.Read(ref _state) != 1 || string.Equals(state.LastSha256, sha256, StringComparison.Ordinal))
                return;

            state.LastSha256 = sha256;
            version = new EditedPdfVersion(path, Path.GetFileName(path), size, ++_versionCount);
            _latestVersion = version;
            _channel.Writer.TryWrite(version);
        }

        _logger.LogInformation(
            "PDF editado disponible para el pedido {RequestId}: {FileName} ({Bytes} bytes), versión {Version}",
            _workspace.RequestId, version.FileName, version.Size, version.Version);
    }

    private static bool TryReadPdf(string path, out long size, out string sha256)
    {
        size = 0;
        sha256 = string.Empty;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var length = stream.Length;
            if (length < PdfHeader.Length + PdfTrailer.Length) return false;

            Span<byte> header = stackalloc byte[PdfHeader.Length];
            stream.ReadExactly(header);
            if (!header.SequenceEqual(PdfHeader)) return false;

            var trailer = new byte[(int)Math.Min(TrailerWindowBytes, length)];
            stream.Seek(length - trailer.Length, SeekOrigin.Begin);
            stream.ReadExactly(trailer);
            if (trailer.AsSpan().IndexOf(PdfTrailer) < 0) return false;

            stream.Seek(0, SeekOrigin.Begin);
            var hash = SHA256.HashData(stream);
            if (stream.Length != length) return false;

            size = length;
            sha256 = Convert.ToHexString(hash);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class PdfFileState
    {
        public long Generation { get; set; }
        public long LastEventAt { get; set; }
        public bool Running { get; set; }
        public string? LastSha256 { get; set; }
        public Task? Worker { get; set; }
    }
}
