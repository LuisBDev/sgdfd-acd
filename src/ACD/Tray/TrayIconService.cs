using System.Diagnostics;
using System.Reflection;
using ACD.Configuration;
using ACD.Update;
using Microsoft.Extensions.Options;

namespace ACD.Tray;

/// <summary>
///     Aloja el NotifyIcon en un thread STA dedicado.
///     Implementa IHostedService para integrarse con Generic Host lifetime.
///     Implementa ITrayStateNotifier para que otros servicios puedan actualizar el estado del icono.
/// </summary>
public sealed class TrayIconService : IHostedService, ITrayStateNotifier, IDisposable
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TrayIconService> _logger;
    private readonly IOptions<AcdOptions> _options;

    private readonly TaskCompletionSource _staReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lazy<IUpdateTrigger> _updateTrigger;

    private TrayState _currentState = TrayState.Ready;
    private ContextMenuStrip? _contextMenu;
    private NotifyIcon? _notifyIcon;
    private string? _pendingUpdateVersion;

    private Thread? _staThread;
    private ToolStripMenuItem? _statusItem;
    private SynchronizationContext? _syncContext;
    private ToolStripMenuItem? _updateItem;
    private int _updateProgress;

    public TrayIconService(
        IHostApplicationLifetime lifetime,
        IOptions<AcdOptions> options,
        ILogger<TrayIconService> logger,
        Lazy<IUpdateTrigger> updateTrigger)
    {
        _lifetime = lifetime;
        _options = options;
        _logger = logger;
        _updateTrigger = updateTrigger;
    }

    public void Dispose()
    {
        _notifyIcon?.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _staThread = new Thread(RunSta)
        {
            IsBackground = true,
            Name = "TrayIconSTA"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        return _staReady.Task;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_syncContext is null) return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _syncContext.Post(_ =>
        {
            try
            {
                if (_notifyIcon is not null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }

                Application.ExitThread();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al eliminar el icono de la bandeja");
            }
            finally
            {
                tcs.TrySetResult();
            }
        }, null);

        _staThread?.Join(TimeSpan.FromSeconds(3));
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    public void SetState(TrayState state)
    {
        if (_syncContext is null) return;

        _syncContext.Post(_ =>
        {
            _currentState = state;
            if (_notifyIcon is not null) _notifyIcon.Icon = CreateIcon(state);
            if (_statusItem is not null) _statusItem.Text = $"Estado: {Describe(state)}";
        }, null);
    }

    public void NotifyUpdateAvailable(string version)
    {
        if (_syncContext is null) return;

        _syncContext.Post(_ =>
        {
            _pendingUpdateVersion = version;
            if (_updateItem is not null && !_updateTrigger.Value.IsBusy)
            {
                _updateItem.Text = $"Instalar actualización v{version} y reiniciar";
                _updateItem.Enabled = true;
            }

            _notifyIcon?.ShowBalloonTip(
                5000,
                "Actualización de ACD",
                $"La versión {version} está lista para instalar.",
                ToolTipIcon.Info);
        }, null);
    }

    public void NotifyUpdateProgress(int percent)
    {
        if (_syncContext is null) return;

        _syncContext.Post(_ =>
        {
            _updateProgress = percent;
            if (_updateItem is not null)
                _updateItem.Text = percent < 100
                    ? $"Descargando actualización… {percent}%"
                    : "Descarga completa";
        }, null);
    }

    public void NotifyUpdateApplying(string version)
    {
        if (_syncContext is null) return;

        _syncContext.Post(_ =>
        {
            _notifyIcon?.ShowBalloonTip(
                6000,
                "Actualizando ACD",
                $"Se instalará la versión {version} y el ACD se reiniciará automáticamente.",
                ToolTipIcon.Info);
        }, null);
    }

    private void RunSta()
    {
        WindowsFormsSynchronizationContext.AutoInstall = false;
        var ctx = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ctx);
        _syncContext = ctx;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";

        _notifyIcon = new NotifyIcon
        {
            Text = "ACD",
            Visible = true,
            Icon = CreateIcon(TrayState.Ready)
        };

        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Estado: Listo") { Enabled = false };
        menu.Items.Add(_statusItem);

        var versionItem = new ToolStripMenuItem($"Versión: {version}") { Enabled = false };
        menu.Items.Add(versionItem);

        menu.Items.Add(new ToolStripSeparator());

        _updateItem = new ToolStripMenuItem("Buscar e instalar actualización");
        _updateItem.Click += (_, _) => UpdateItemClicked();
        menu.Items.Add(_updateItem);

        menu.Items.Add(new ToolStripSeparator());

        var restartItem = new ToolStripMenuItem("Reiniciar");
        restartItem.Click += (_, _) => RestartClicked();
        menu.Items.Add(restartItem);

        var closeItem = new ToolStripMenuItem("Cerrar");
        closeItem.Click += (_, _) => _lifetime.StopApplication();
        menu.Items.Add(closeItem);

        _notifyIcon.ContextMenuStrip = menu;

        // Keep the menu open while an update is in progress so the user can
        // see the status text change (Buscando… → Descargando… → etc.).
        menu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked
                && _updateItem is { Enabled: false })
                e.Cancel = true;
        };

        _contextMenu = menu;

        _staReady.TrySetResult();

        Application.Run(new ApplicationContext());
    }

    private async void UpdateItemClicked()
    {
        if (_updateItem is null) return;

        _logger.LogInformation("El usuario disparó la actualización a demanda");
        _updateItem.Enabled = false;
        _updateItem.Text = "Buscando actualizaciones…";

        UpdateOutcome outcome;
        try
        {
            outcome = await _updateTrigger.Value.UpdateNowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en la actualización a demanda");
            outcome = UpdateOutcome.Failed;
        }

        switch (outcome)
        {
            case UpdateOutcome.Applied:
                // El proceso se está reiniciando; no restaurar el item.
                return;
            case UpdateOutcome.NoUpdatesAvailable:
                ShowBalloon("ACD", "Ya tiene la última versión instalada.", ToolTipIcon.Info);
                break;
            case UpdateOutcome.SessionActive:
                ShowBalloon("ACD", "Hay una firma en curso. Espere a que termine para actualizar.", ToolTipIcon.Warning);
                break;
            case UpdateOutcome.Failed:
                ShowBalloon("ACD", "No se pudo actualizar. Intente nuevamente más tarde.", ToolTipIcon.Error);
                break;
        }

        ResetUpdateItem();
    }

    private void ResetUpdateItem()
    {
        if (_updateItem is null) return;

        _updateItem.Enabled = true;
        _updateItem.Text = _pendingUpdateVersion is null
            ? "Buscar e instalar actualización"
            : $"Instalar actualización v{_pendingUpdateVersion} y reiniciar";
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _notifyIcon?.ShowBalloonTip(4000, title, text, icon);
    }

    private static string Describe(TrayState state) => state switch
    {
        TrayState.Ready => "Listo",
        TrayState.Connected => "Conectado",
        TrayState.Error => "Error",
        _ => state.ToString()
    };

    private void RestartClicked()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath)) Process.Start(exePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al reiniciar el proceso");
        }

        _lifetime.StopApplication();
    }

    private static Icon CreateIcon(TrayState state)
    {
        var color = state switch
        {
            TrayState.Ready => Color.Green,
            TrayState.Connected => Color.Blue,
            TrayState.Error => Color.Red,
            _ => Color.Gray
        };

        using var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(color);

        var hIcon = bitmap.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        return icon;
    }
}