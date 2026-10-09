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
    private static readonly byte[] Docx = [80, 75, 3, 4];

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
    public async Task conversionQueNoTermina_lanzaCONVERSION_TIMEOUT_yMataElProcesoCreado()
    {
        var killed = new ManualResetEventSlim();
        _processes.OnKill = _ => killed.Set();
        var service = CreateService(
            () =>
            {
                _processes.Start(CreatedWordPid);
                return new FakeWord(new FakeDocuments(_ => killed.Wait()));
            },
            TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<ConversionException>(
            () => service.ConvertDocxToPdfAsync(Docx, CancellationToken.None));

        Assert.Equal(ErrorCatalog.ConversionTimeout, error.Code);
        Assert.Equal([CreatedWordPid], _processes.Killed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_tempRoot));
    }

    [Fact]
    public async Task conversionesConcurrentes_seSerializan()
    {
        var running = 0;
        var maxRunning = 0;
        var documents = new FakeDocuments(_ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            Thread.Sleep(150);
            Interlocked.Decrement(ref running);
        });
        var service = CreateService(() => new FakeWord(documents), TimeSpan.FromSeconds(10));

        var results = await Task.WhenAll(
            service.ConvertDocxToPdfAsync(Docx, CancellationToken.None),
            service.ConvertDocxToPdfAsync(Docx, CancellationToken.None));

        Assert.Equal(1, maxRunning);
        Assert.All(results, pdf => Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_tempRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, true);
    }

    private WordComConversionService CreateService(Func<object?> wordFactory, TimeSpan timeout) =>
        new(wordFactory, _processes.CurrentIds, _processes, timeout, _tempRoot, NullLogger<WordComConversionService>.Instance);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            Interlocked.CompareExchange(ref target, value, current);
    }

    public sealed class FakeWordProcesses(params int[] initial) : IProcessKiller
    {
        private readonly ConcurrentDictionary<int, byte> _running = new(initial.Select(pid => KeyValuePair.Create(pid, (byte)0)));
        private readonly ConcurrentQueue<int> _killed = new();

        public Action<int> OnKill { get; set; } = _ => { };

        public IReadOnlyCollection<int> Killed => _killed.ToArray();

        public void Start(int pid) => _running[pid] = 0;

        public IReadOnlySet<int> CurrentIds() => _running.Keys.ToHashSet();

        public void Kill(int processId)
        {
            _killed.Enqueue(processId);
            _running.TryRemove(processId, out _);
            OnKill(processId);
        }
    }

    public sealed class FakeWord(FakeDocuments documents)
    {
        public bool Visible { get; set; } = true;
        public int DisplayAlerts { get; set; } = -1;
        public FakeDocuments Documents { get; } = documents;

        public void Quit(int saveChanges)
        {
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
