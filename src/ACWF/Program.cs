using ACWF.Configuration;
using ACWF.Firma;
using ACWF.System;
using ACWF.Update;
using ACWF.WebSocket;
using Microsoft.Extensions.Options;
using Serilog;
using Velopack;

// Alias to avoid ambiguity with Velopack.UpdateOptions
using AppUpdateOptions = ACWF.Configuration.UpdateOptions;

// Step 1: Velopack initialization — MUST be the first statement.
// Handles install/update/uninstall lifecycle events and may exit the process.
VelopackApp.Build()
    .WithFirstRun(_ =>
    {
        string exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        UriSchemeHelper.EnsureRegistered("acwf", exePath);
        UriSchemeHelper.EnsureRegistered("acwf-dev", exePath);
    })
    .WithAfterUpdateFastCallback(_ =>
    {
        string exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        UriSchemeHelper.EnsureRegistered("acwf", exePath);
        UriSchemeHelper.EnsureRegistered("acwf-dev", exePath);
    })
    .WithBeforeUninstallFastCallback(_ =>
    {
        UriSchemeHelper.Unregister("acwf");
        UriSchemeHelper.Unregister("acwf-dev");
    })
    .Run();

// Step 2: Determine environment and derived identifiers.
string env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? "Production";

string packId = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
    ? "ACWF-Dev"
    : "ACWF";

string uriScheme = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
    ? "acwf-dev"
    : "acwf";

string mutexSuffix = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
    ? "Dev"
    : "Prod";

// Step 3: Single-instance guard — exit silently if another instance of this variant is running.
using IDisposable instanceGuard = InstanceGuard.Acquire(mutexSuffix);

// Step 4: Build the ASP.NET Core Generic Host.
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseKestrel(o =>
{
    int port = builder.Configuration.GetValue<int>("Acwf:Port", 7272);
    o.ListenLocalhost(port);
});

// Step 5: Configure Serilog as the logging provider.
string logDir = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    packId,
    "logs");

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.File(
        path: System.IO.Path.Combine(logDir, "acwf-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 10_000_000));

// Step 6: Register all services.
builder.Services.Configure<AcwfOptions>(builder.Configuration.GetSection("Acwf"));
builder.Services.Configure<AppUpdateOptions>(builder.Configuration.GetSection("Update"));

builder.Services.AddSingleton<ISessionGate, SessionGate>();
builder.Services.AddScoped<IFileDepositService, FileDepositService>();
builder.Services.AddScoped<IFirmaWatcherService, FirmaWatcherService>();

// TrayIconService registered as singleton, served via both its concrete type and the notifier interface.
builder.Services.AddSingleton<TrayIconService>();
builder.Services.AddSingleton<ITrayStateNotifier>(sp => sp.GetRequiredService<TrayIconService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrayIconService>());

// UpdateService registered as singleton BackgroundService and trigger interface.
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<IUpdateTrigger>(sp => sp.GetRequiredService<UpdateService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

// Lazy<IUpdateTrigger> breaks the circular dependency between TrayIconService and UpdateService.
builder.Services.AddSingleton(sp => new Lazy<IUpdateTrigger>(() => sp.GetRequiredService<IUpdateTrigger>()));

// Step 7: Build the application and configure the middleware pipeline.
var app = builder.Build();
app.UseWebSockets();
app.UseMiddleware<AcwfWebSocketMiddleware>();

// Step 8: Write port lock file and register URI scheme (idempotent at every run).
var acwfOptions = app.Services.GetRequiredService<IOptions<AcwfOptions>>().Value;
PortRegistry.Write(packId, acwfOptions.Port);

string exePathForScheme = Environment.ProcessPath
    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
UriSchemeHelper.EnsureRegistered(uriScheme, exePathForScheme);

// Step 9: Register cleanup on graceful shutdown.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    PortRegistry.Delete(packId);
    app.Logger.LogInformation("Port lock file deleted for {PackId}", packId);
});

app.Logger.LogInformation(
    "ACWF v{Version} starting — environment: {Env}, packId: {PackId}, port: {Port}",
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0",
    env, packId, acwfOptions.Port);

// Step 10: Run — blocks until host shutdown.
app.Run();
