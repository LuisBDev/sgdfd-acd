using System.Threading.Channels;
using ACD.Files;

namespace ACD.DocumentWorkspace;

public sealed class WorkspaceWatcher : IWorkspaceWatcher
{
    private const string WordFilter = "*.docx";
    private const string LockFilePrefix = "~$";
    private const int NotStarted = 0;
    private const int Running = 1;
    private const int Disposed = 2;
    private const int MaxReadRetries = 5;

    private static readonly TimeSpan StabilizationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly StableFileProbeOptions ProbeOptions = new()
    {
        OpenShare = FileShare.ReadWrite | FileShare.Delete
    };

    private readonly Channel<WordFileInfo?> _channel = Channel.CreateUnbounded<WordFileInfo?>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _debounce;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly ILogger<WorkspaceWatcher> _logger;
    private readonly IStableFileProbe _probe;
    private readonly TimeSpan _retryDelay;
    private readonly WorkspaceFileStore _store;

    private WordFileInfo? _current;
    private CancellationTokenRegistration _externalRegistration;
    private long _generation;
    private CancellationTokenSource? _probeCts;
    private long _quietUntil;
    private int _readRetries;
    private bool _snapshotReady;
    private int _state;
    private FileSystemWatcher? _watcher;
    private Task? _worker;
    private bool _workerRunning;

    public WorkspaceWatcher(
        string directory,
        WorkspaceFileStore store,
        IStableFileProbe probe,
        TimeSpan debounce,
        ILogger<WorkspaceWatcher> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _store = store;
        _probe = probe;
        _debounce = debounce < TimeSpan.Zero ? TimeSpan.Zero : debounce;
        _retryDelay = _debounce > MinRetryDelay ? _debounce : MinRetryDelay;
        _logger = logger;
    }

    public WordFileInfo? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public ChannelReader<WordFileInfo?> Changes => _channel.Reader;

    public void Start(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == Disposed, this);
        if (Interlocked.CompareExchange(ref _state, Running, NotStarted) != NotStarted)
            throw new InvalidOperationException("WorkspaceWatcher ya fue iniciado");

        Directory.CreateDirectory(_directory);
        lock (_gate) _watcher = CreateFileSystemWatcher();

        if (ct.CanBeCanceled)
            _externalRegistration = ct.Register(static state => ((WorkspaceWatcher)state!).CancelBestEffort(), this);

        var initialSnapshotRead = TryDescribeLatestWord(out var initial);
        lock (_gate)
        {
            _current = initial;
            _snapshotReady = true;
            if (!initialSnapshotRead)
                MarkEventLocked();
            if (_generation > 0)
                StartWorkerLocked();
        }

        _logger.LogInformation(
            "WorkspaceWatcher iniciado en {Directory}; Word activo: {FileName}",
            _directory, initial?.FileName);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _state, Disposed) == Disposed) return;

        CancelBestEffort();
        _externalRegistration.Dispose();

        Task? worker;
        lock (_gate)
        {
            ReleaseFileSystemWatcher();
            worker = _worker;
        }

        _channel.Writer.TryComplete();

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "El worker del WorkspaceWatcher terminó con error durante el cierre");
            }
        }

        _cts.Dispose();
    }

    private FileSystemWatcher CreateFileSystemWatcher()
    {
        var watcher = new FileSystemWatcher(_directory)
        {
            Filter = WordFilter,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024
        };
        watcher.Created += OnFileEvent;
        watcher.Changed += OnFileEvent;
        watcher.Deleted += OnFileEvent;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void ReleaseFileSystemWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;

        watcher.EnableRaisingEvents = false;
        watcher.Created -= OnFileEvent;
        watcher.Changed -= OnFileEvent;
        watcher.Deleted -= OnFileEvent;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnWatcherError;
        watcher.Dispose();
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
        if (IsLockFile(e.Name)) return;
        ScheduleSafely();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsLockFile(e.Name) && IsLockFile(e.OldName)) return;
        ScheduleSafely();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "Error del vigilante de {Directory}; se recrea y se vuelve a escanear", _directory);
        RecreateFileSystemWatcher();
        ScheduleSafely();
    }

    private void RecreateFileSystemWatcher()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _state) != Running || _cts.IsCancellationRequested) return;

            ReleaseFileSystemWatcher();
            try
            {
                Directory.CreateDirectory(_directory);
                _watcher = CreateFileSystemWatcher();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(ex, "No se pudo recrear el vigilante de {Directory}", _directory);
            }
        }
    }

    private static bool IsLockFile(string? name) =>
        name is not null && name.StartsWith(LockFilePrefix, StringComparison.Ordinal);

    private void ScheduleSafely()
    {
        try
        {
            Schedule();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo programar la revisión de {Directory}", _directory);
        }
    }

    private void Schedule()
    {
        CancellationTokenSource? pendingProbe;
        lock (_gate)
        {
            if (Volatile.Read(ref _state) != Running || _cts.IsCancellationRequested) return;

            MarkEventLocked();
            _readRetries = 0;
            pendingProbe = _probeCts;
            _probeCts = null;
            if (_snapshotReady)
                StartWorkerLocked();
        }

        CancelProbe(pendingProbe);
    }

    private void MarkEventLocked() => DeferLocked(_debounce);

    private void DeferLocked(TimeSpan quietPeriod)
    {
        _generation++;
        _quietUntil = Environment.TickCount64 + (long)quietPeriod.TotalMilliseconds;
    }

    private bool TryScheduleRetryLocked()
    {
        if (_readRetries >= MaxReadRetries)
        {
            _logger.LogWarning(
                "No se pudo leer el Word activo de {Directory} tras {Retries} reintentos; se espera el siguiente evento",
                _directory, _readRetries);
            return false;
        }

        _readRetries++;
        DeferLocked(_retryDelay);
        return true;
    }

    private void StartWorkerLocked()
    {
        if (_workerRunning) return;

        _workerRunning = true;
        var token = _cts.Token;
        _worker = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    private static void CancelProbe(CancellationTokenSource? probe)
    {
        try
        {
            probe?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                long generation;
                while (!TryGetQuietGeneration(out generation, out var remaining))
                    await Task.Delay(remaining, token).ConfigureAwait(false);

                var read = await TryRefreshAsync(generation, token).ConfigureAwait(false);

                lock (_gate)
                {
                    if (read) _readRetries = 0;
                    if (_generation != generation) continue;
                    if (!read && TryScheduleRetryLocked()) continue;
                    _workerRunning = false;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private bool TryGetQuietGeneration(out long generation, out TimeSpan remaining)
    {
        lock (_gate)
        {
            generation = _generation;
            remaining = TimeSpan.FromMilliseconds(_quietUntil - Environment.TickCount64);
            return remaining <= TimeSpan.Zero;
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_gate) return _generation == generation;
    }

    private async Task WaitForStableCandidateAsync(long generation, CancellationToken token)
    {
        var candidate = WorkspaceFileNames.FindLatestWord(_directory);
        if (candidate is null) return;

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        probeCts.CancelAfter(StabilizationTimeout);
        lock (_gate)
        {
            if (_generation != generation) return;
            _probeCts = probeCts;
        }

        try
        {
            await _probe.WaitUntilStableAsync(candidate, ProbeOptions, probeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _logger.LogDebug("El Word {Path} no se estabilizó antes del siguiente evento o del plazo", candidate);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_probeCts, probeCts))
                    _probeCts = null;
            }
        }
    }

    private async Task<bool> TryRefreshAsync(long generation, CancellationToken token)
    {
        try
        {
            await WaitForStableCandidateAsync(generation, token).ConfigureAwait(false);
            if (IsCurrentGeneration(generation))
                Refresh(generation);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "No se pudo leer el Word activo de {Directory}; se reintentará", _directory);
            return false;
        }
    }

    private void Refresh(long generation)
    {
        if (LatestWordMatchesCurrent()) return;
        var snapshot = _store.DescribeLatestWord(_directory);

        lock (_gate)
        {
            if (Volatile.Read(ref _state) != Running || _generation != generation || snapshot == _current) return;

            _current = snapshot;
            _channel.Writer.TryWrite(snapshot);
        }

        _logger.LogInformation(
            "Word activo de {Directory}: {FileName} ({Bytes} bytes)",
            _directory, snapshot?.FileName, snapshot?.Size);
    }

    private bool LatestWordMatchesCurrent()
    {
        var latest = WorkspaceFileNames.FindLatestWord(_directory);
        var current = Current;
        if (latest is null || current is null)
            return latest is null && current is null;

        var file = new FileInfo(latest);
        return file.Exists
               && string.Equals(file.Name, current.FileName, StringComparison.Ordinal)
               && file.Length == current.Size
               && WorkspaceFileNames.EffectiveChangedAt(file) == current.ChangedAt;
    }

    private bool TryDescribeLatestWord(out WordFileInfo? snapshot)
    {
        try
        {
            snapshot = _store.DescribeLatestWord(_directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "No se pudo leer la foto inicial de {Directory}; se reprograma la lectura", _directory);
            snapshot = null;
            return false;
        }
    }
}
