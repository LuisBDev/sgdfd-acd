using System.Collections.Concurrent;
using System.Text;
using ACD.DocumentWorkspace;
using ACD.WebSocket.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACD.Tests.DocumentWorkspace;

public sealed class WordComConversionServiceTests : IDisposable
{
    private const int UserWordPid = 100;
    private const int CreatedWordPid = 200;
    private const int UserWordOpenedDuringConversionPid = 300;
    private static readonly byte[] Docx = [80, 75, 3, 4];
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "acd-conv-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWordProcesses _processes = new(UserWordPid);

    [Fact]
    public async Task sinProgIdDeWord_lanzaWORD_NOT_INSTALLED()
    {
        var service = CreateService(() => null, TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<ConversionException>(
            () => service.ConvertDocxToPdfAsync(Docx, CancellationToken.None));

        Assert.Equal(ErrorCatalog.WordNotInstalled, error.Code);
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public async Task conversionQueNoTermina_lanzaCONVERSION_TIMEOUT_yMataSoloElProcesoDeAutomatizacionCreado()
    {
        var killed = new ManualResetEventSlim();
        _processes.OnKill = _ => killed.Set();
        var service = CreateService(
            () =>
            {
                _processes.Start(CreatedWordPid, automation: true);
                _processes.Start(UserWordOpenedDuringConversionPid, automation: false);
                return new FakeWord(new FakeDocuments(_ => killed.Wait(BoundedWait)), () => { });
            },
            TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<ConversionException>(
            () => service.ConvertDocxToPdfAsync(Docx, CancellationToken.None));

        Assert.Equal(ErrorCatalog.ConversionTimeout, error.Code);
        Assert.Equal([CreatedWordPid], _processes.Killed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_tempRoot));
    }

    [Fact]
    public async Task sinProcesoDeAutomatizacionNuevo_noTocaElWordYLanzaCONVERSION_FAILED()
    {
        var word = new FakeWord(new FakeDocuments(_ => { }), () => { });
        var service = CreateService(
            () =>
            {
                _processes.Start(UserWordOpenedDuringConversionPid, automation: false);
                return word;
            },
            TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<ConversionException>(
            () => service.ConvertDocxToPdfAsync(Docx, CancellationToken.None));

        Assert.Equal(ErrorCatalog.ConversionFailed, error.Code);
        Assert.True(word.Visible);
        Assert.False(word.QuitCalled);
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public async Task conversionExitosaConWordQueNoTermina_mataElProcesoCreado()
    {
        var service = CreateService(
            () =>
            {
                _processes.Start(CreatedWordPid, automation: true);
                return new FakeWord(new FakeDocuments(_ => { }), () => { });
            },
            TimeSpan.FromSeconds(5));

        var pdf = await service.ConvertDocxToPdfAsync(Docx, CancellationToken.None);

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf));
        Assert.Equal([CreatedWordPid], _processes.Killed);
    }

    [Fact]
    public async Task conversionesConcurrentes_seSerializan()
    {
        var running = 0;
        var maxRunning = 0;
        var nextPid = CreatedWordPid;
        var documents = new FakeDocuments(_ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            Thread.Sleep(150);
            Interlocked.Decrement(ref running);
        });
        var service = CreateService(
            () =>
            {
                var pid = Interlocked.Increment(ref nextPid);
                _processes.Start(pid, automation: true);
                return new FakeWord(documents, () => _processes.Exit(pid));
            },
            TimeSpan.FromSeconds(10));

        var results = await Task.WhenAll(
            service.ConvertDocxToPdfAsync(Docx, CancellationToken.None),
            service.ConvertDocxToPdfAsync(Docx, CancellationToken.None));

        Assert.Equal(1, maxRunning);
        Assert.All(results, pdf => Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf)));
        Assert.Empty(_processes.Killed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_tempRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, true);
    }

    private WordComConversionService CreateService(Func<object?> wordFactory, TimeSpan timeout) =>
        new(
            wordFactory,
            _processes,
            new WordConversionSettings(timeout, TimeSpan.FromMilliseconds(300), _tempRoot),
            NullLogger<WordComConversionService>.Instance);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            Interlocked.CompareExchange(ref target, value, current);
    }

    public sealed class FakeWordProcesses : IWordProcesses
    {
        private readonly ConcurrentDictionary<int, bool> _running = new();
        private readonly ConcurrentQueue<int> _killed = new();

        public FakeWordProcesses(int userPid) => _running[userPid] = false;

        public Action<int> OnKill { get; set; } = _ => { };

        public IReadOnlyCollection<int> Killed => _killed.ToArray();

        public void Start(int pid, bool automation) => _running[pid] = automation;

        public void Exit(int pid) => _running.TryRemove(pid, out _);

        public IReadOnlySet<int> CurrentIds() => _running.Keys.ToHashSet();

        public bool IsAutomationInstance(int processId) => _running.TryGetValue(processId, out var automation) && automation;

        public void Kill(int processId)
        {
            _killed.Enqueue(processId);
            _running.TryRemove(processId, out _);
            OnKill(processId);
        }
    }

    public sealed class FakeWord(FakeDocuments documents, Action onQuit)
    {
        public bool Visible { get; set; } = true;
        public int DisplayAlerts { get; set; } = -1;
        public bool QuitCalled { get; private set; }
        public FakeDocuments Documents { get; } = documents;

        public void Quit(int saveChanges)
        {
            QuitCalled = true;
            onQuit();
        }
    }

    public sealed class FakeDocuments(Action<string> onOpen)
    {
        public FakeDocument Open(string FileName, bool ConfirmConversions, bool ReadOnly, bool AddToRecentFiles)
        {
            onOpen(FileName);
            return new FakeDocument();
        }
    }

    public sealed class FakeDocument
    {
        public void ExportAsFixedFormat(string OutputFileName, int ExportFormat) =>
            File.WriteAllBytes(OutputFileName, Encoding.ASCII.GetBytes("%PDF-1.7 fake"));

        public void Close(int saveChanges)
        {
        }
    }
}
