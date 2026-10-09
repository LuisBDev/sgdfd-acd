using System.Runtime.InteropServices;
using System.Text;
using ACD.WebSocket.Messages;

namespace ACD.DocumentWorkspace;

public sealed class WordComConversionService : IConversionService, IDisposable
{
    private const string WordProgId = "Word.Application";
    private const string InputFileName = "in.docx";
    private const string OutputFileName = "out.pdf";
    private const int PdfExportFormat = 17;
    private const int DoNotSaveChanges = 0;
    private const int NoAlerts = 0;
    private static readonly byte[] PdfSignature = Encoding.ASCII.GetBytes("%PDF");
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IWordProcesses _processes;
    private readonly TimeSpan _exitGracePeriod;
    private readonly ILogger<WordComConversionService> _logger;
    private readonly string _tempRoot;
    private readonly TimeSpan _timeout;
    private readonly Func<object?> _wordFactory;

    public WordComConversionService(
        Func<object?> wordFactory,
        IWordProcesses processes,
        WordConversionSettings settings,
        ILogger<WordComConversionService> logger)
    {
        _wordFactory = wordFactory;
        _processes = processes;
        _timeout = settings.Timeout;
        _exitGracePeriod = settings.ExitGracePeriod;
        _tempRoot = settings.TempRoot;
        _logger = logger;
    }

    public static object? CreateWordApplication() =>
        Type.GetTypeFromProgID(WordProgId) is { } wordType ? Activator.CreateInstance(wordType) : null;

    public async Task<byte[]> ConvertDocxToPdfAsync(byte[] docx, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var folder = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var input = Path.Combine(folder, InputFileName);
            var output = Path.Combine(folder, OutputFileName);
            await File.WriteAllBytesAsync(input, docx, ct).ConfigureAwait(false);

            var processes = new CreatedWordProcesses(_processes.CurrentIds());
            var conversion = StaWorker.RunAsync("acd-word-conversion", () => Convert(input, output, processes));
            await WaitForConversionAsync(conversion, processes, ct).ConfigureAwait(false);
            await EnsureCreatedWordExitedAsync(processes).ConfigureAwait(false);
            return await ReadPdfAsync(output, ct).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(folder);
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task WaitForConversionAsync(Task conversion, CreatedWordProcesses processes, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        var finished = await Task.WhenAny(conversion, Task.Delay(Timeout.Infinite, deadline.Token)).ConfigureAwait(false);

        if (finished != conversion)
        {
            _logger.LogWarning("La conversión de Word a PDF superó {Timeout}; se cierra el Word creado", _timeout);
            KillCreatedWord(processes);
            await Task.WhenAny(conversion, Task.Delay(_exitGracePeriod, CancellationToken.None)).ConfigureAwait(false);
            ObserveFault(conversion);
            ct.ThrowIfCancellationRequested();
            throw new ConversionException(ErrorCatalog.ConversionTimeout);
        }

        try
        {
            await conversion.ConfigureAwait(false);
        }
        catch (ConversionException ex) when (ex.Code == ErrorCatalog.ConversionFailed)
        {
            _logger.LogError(ex.InnerException, "Word no pudo convertir el documento a PDF");
            await EnsureCreatedWordExitedAsync(processes).ConfigureAwait(false);
            throw;
        }
    }

    private void Convert(string input, string output, CreatedWordProcesses processes)
    {
        object? application = null;
        dynamic? document = null;
        var owned = false;
        try
        {
            application = _wordFactory() ?? throw new ConversionException(ErrorCatalog.WordNotInstalled);
            owned = processes.Record(NewAutomationProcesses(processes.Before));
            if (!owned)
            {
                _logger.LogWarning("No se identificó el proceso de Word creado para la conversión; no se usará esa instancia");
                throw new ConversionException(ErrorCatalog.ConversionFailed);
            }

            dynamic word = application;
            word.Visible = false;
            word.DisplayAlerts = NoAlerts;
            document = word.Documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false);
            document.ExportAsFixedFormat(output, PdfExportFormat);
        }
        catch (Exception ex) when (ex is not ConversionException)
        {
            throw new ConversionException(ErrorCatalog.ConversionFailed, ex);
        }
        finally
        {
            CloseQuietly(document);
            if (owned)
                QuitQuietly(application);
            ReleaseQuietly(document);
            ReleaseQuietly(application);
        }
    }

    private IReadOnlySet<int> NewAutomationProcesses(IReadOnlySet<int> before) =>
        _processes.CurrentIds()
            .Except(before)
            .Where(_processes.IsAutomationInstance)
            .ToHashSet();

    private void KillCreatedWord(CreatedWordProcesses processes)
    {
        foreach (var processId in processes.Recorded ?? NewAutomationProcesses(processes.Before))
            _processes.Kill(processId);
    }

    private async Task EnsureCreatedWordExitedAsync(CreatedWordProcesses processes)
    {
        if (processes.Recorded is not { Count: > 0 } created)
            return;

        var deadline = DateTime.UtcNow + _exitGracePeriod;
        var remaining = StillRunning(created);
        while (remaining.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(ExitPollInterval, CancellationToken.None).ConfigureAwait(false);
            remaining = StillRunning(created);
        }

        foreach (var processId in remaining)
        {
            _logger.LogWarning("El Word {ProcessId} creado para la conversión no terminó; se cierra", processId);
            _processes.Kill(processId);
        }
    }

    private IReadOnlySet<int> StillRunning(IReadOnlySet<int> created) =>
        created.Intersect(_processes.CurrentIds()).ToHashSet();

    private static async Task<byte[]> ReadPdfAsync(string output, CancellationToken ct)
    {
        if (!File.Exists(output))
            throw new ConversionException(ErrorCatalog.ConversionFailed);

        var pdf = await File.ReadAllBytesAsync(output, ct).ConfigureAwait(false);
        return pdf.AsSpan().StartsWith(PdfSignature)
            ? pdf
            : throw new ConversionException(ErrorCatalog.ConversionFailed);
    }

    private static void CloseQuietly(dynamic? document)
    {
        if (document is null) return;
        try
        {
            document.Close(DoNotSaveChanges);
        }
        catch (Exception)
        {
        }
    }

    private static void QuitQuietly(object? application)
    {
        if (application is null) return;
        try
        {
            ((dynamic)application).Quit(DoNotSaveChanges);
        }
        catch (Exception)
        {
        }
    }

    private static void ReleaseQuietly(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }

    private static void ObserveFault(Task task) =>
        task.ContinueWith(completed => completed.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private void DeleteQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo borrar la carpeta temporal de conversión {Folder}", folder);
        }
    }

    private sealed class CreatedWordProcesses(IReadOnlySet<int> before)
    {
        private volatile IReadOnlySet<int>? _recorded;

        public IReadOnlySet<int> Before { get; } = before;

        public IReadOnlySet<int>? Recorded => _recorded;

        public bool Record(IReadOnlySet<int> created)
        {
            _recorded = created;
            return created.Count > 0;
        }
    }
}
