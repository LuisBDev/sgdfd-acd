using ACD.Hosting;
using ACD.Tray;
using ACD.WebSocket;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Sources;
using AppUpdateOptions = ACD.Configuration.UpdateOptions;
using VelopackUpdateOptions = Velopack.UpdateOptions;

namespace ACD.Update;

/// <summary>
///     Background service that periodically checks and downloads Velopack updates.
///     Installation is on-demand: only applied when the user triggers it from the tray
///     and there is no active signing session.
///
///     Startup behavior:
///     - When launched manually by the user: runs an initial check immediately (after a
///       short stabilization delay) and then every <c>CheckIntervalHours</c> hours.
///     - When launched via URI scheme (e.g. acd://…): skips the startup check to avoid
///       unnecessary network activity; the periodic timer still runs normally.
///
///     A balloon tip is shown every time a pending update is detected, regardless of
///     whether the detection was triggered at startup or by the periodic timer.
/// </summary>
public sealed class UpdateService : BackgroundService, IUpdateTrigger
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LaunchContext _launchContext;
    private readonly ILogger<UpdateService> _logger;
    private readonly AppUpdateOptions _options;
    private readonly ISessionGate _sessionGate;
    private readonly ITrayStateNotifier _trayNotifier;

    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private UpdateInfo? _pendingUpdate;
    private UpdateManager? _updateManager;

    public UpdateService(
        IOptions<AppUpdateOptions> options,
        ISessionGate sessionGate,
        ITrayStateNotifier trayNotifier,
        ILogger<UpdateService> logger,
        IHostApplicationLifetime lifetime,
        LaunchContext launchContext)
    {
        _options = options.Value;
        _sessionGate = sessionGate;
        _trayNotifier = trayNotifier;
        _logger = logger;
        _lifetime = lifetime;
        _launchContext = launchContext;
    }

    public bool HasPendingUpdate => _pendingUpdate is not null;
    public string? PendingVersion { get; private set; }
    public bool IsBusy { get; private set; }
    public int LastProgress { get; private set; }

    /// <summary>
    ///     Flujo a demanda de un solo gesto: descarga (si hace falta), aplica y reinicia el ACD.
    ///     Aborta sin efectos si hay una firma en curso.
    /// </summary>
    public async Task<UpdateOutcome> UpdateNowAsync()
    {
        if (IsBusy) return UpdateOutcome.Failed;
        if (_sessionGate.IsActive) return UpdateOutcome.SessionActive;

        IsBusy = true;
        try
        {
            if (!HasPendingUpdate)
                await CheckAndDownloadAsync().ConfigureAwait(false);

            if (!HasPendingUpdate)
                return UpdateOutcome.NoUpdatesAvailable;

            // Revalidar: una firma pudo iniciarse mientras se descargaba.
            if (_sessionGate.IsActive)
                return UpdateOutcome.SessionActive;

            ApplyUpdate();
            return UpdateOutcome.Applied;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en la actualización a demanda");
            return UpdateOutcome.Failed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Allow the host to stabilize before the first check.
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);

        // Skip the startup check when the process was activated via URI scheme.
        // The user did not open ACD manually, so performing an update check at this
        // point would be unexpected and wasteful. The periodic timer below still runs
        // normally so the check will happen within the next CheckIntervalHours window.
        if (_launchContext.IsUriSchemeInvocation)
        {
            _logger.LogInformation(
                "UpdateService: process was started via URI scheme — skipping startup update check.");
        }
        else
        {
            await CheckAndDownloadAsync().ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromHours(_options.CheckIntervalHours),
                stoppingToken).ConfigureAwait(false);

            await CheckAndDownloadAsync().ConfigureAwait(false);
        }
    }

    // Aplica el update pendiente reiniciando el proceso vía Velopack.
    private void ApplyUpdate()
    {
        if (_pendingUpdate is null || _updateManager is null)
        {
            _logger.LogWarning("ApplyUpdate llamado pero no hay actualización pendiente disponible");
            return;
        }

        _logger.LogInformation(
            "Aplicando actualización {Version}",
            _pendingUpdate.TargetFullRelease.Version);

        // UpdateInfo tiene una conversión implícita a VelopackAsset.
        _updateManager.WaitExitThenApplyUpdates((VelopackAsset)_pendingUpdate);
        _lifetime.StopApplication();
    }

    private async Task CheckAndDownloadAsync()
    {
        if (string.IsNullOrEmpty(_options.RepoUrl))
        {
            _logger.LogDebug("UpdateService: RepoUrl no configurado, omitiendo verificación de actualizaciones");
            return;
        }

        // Serializa el chequeo automático con el disparo a demanda para evitar descargas duplicadas.
        await _checkLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HasPendingUpdate) return;

            _logger.LogInformation(
                "Verificando actualizaciones en {RepoUrl} (canal={Channel}, prerelease={Pre})",
                _options.RepoUrl, _options.Channel, _options.IncludePrerelease);

            // Fuente GitHub Releases: consulta la API pública del repositorio.
            // prerelease=true permite que la variante dev incluya versiones pre-release.
            var source = new GithubSource(
                _options.RepoUrl,
                string.IsNullOrWhiteSpace(_options.AccessToken) ? null : _options.AccessToken,
                _options.IncludePrerelease);

            _updateManager = new UpdateManager(source, new VelopackUpdateOptions
            {
                AllowVersionDowngrade = false,
                // El canal debe coincidir con el --channel usado en CI (stable / dev).
                ExplicitChannel = string.IsNullOrWhiteSpace(_options.Channel) ? null : _options.Channel
            });

            var updateInfo = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (updateInfo is null)
            {
                _logger.LogInformation("No hay actualizaciones disponibles");
                return;
            }

            _logger.LogInformation(
                "Actualización disponible: {Version}",
                updateInfo.TargetFullRelease.Version);

            await _updateManager.DownloadUpdatesAsync(updateInfo, percent =>
            {
                LastProgress = percent;
                _trayNotifier.NotifyUpdateProgress(percent);

                if (percent is 0 or 50 or 100) _logger.LogInformation("Progreso de descarga de actualización: {Percent}%", percent);
            }).ConfigureAwait(false);

            _pendingUpdate = updateInfo;
            PendingVersion = updateInfo.TargetFullRelease.Version.ToString();
            _trayNotifier.NotifyUpdateAvailable(PendingVersion);

            _logger.LogInformation(
                "Actualización {Version} descargada y lista para aplicar",
                updateInfo.TargetFullRelease.Version);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("La verificación de actualizaciones aún no está disponible: {Message}", ex.Message);
        }
        finally
        {
            _checkLock.Release();
        }
    }
}
