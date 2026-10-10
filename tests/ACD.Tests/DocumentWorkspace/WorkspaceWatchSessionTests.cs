using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using ACD.Configuration;
using ACD.DocumentWorkspace;
using ACD.WebSocket;
using ACD.WebSocket.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACD.Tests.DocumentWorkspace;

public sealed class WorkspaceWatchSessionTests : IAsyncDisposable
{
    private const string Anio = "2026";
    private const string NumeroEmision = "0000121977";

    private static readonly WordFileInfo CurrentWord = new(
        "Oficio_20261009-075123.docx",
        new DateTime(2026, 10, 9, 7, 51, 23),
        2048,
        new string('a', 64));

    private readonly FakeWatcherFactory _factory = new(CurrentWord);
    private readonly string _requestId = Guid.NewGuid().ToString();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acd-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceWatchSession _session;
    private readonly RecordingWebSocket _webSocket = new();

    public WorkspaceWatchSessionTests()
    {
        var paths = new WorkspacePaths(new DocumentWorkspaceOptions { RootDirectory = _root });
        _session = new WorkspaceWatchSession(_factory, paths, NullLogger.Instance, "test-session");
    }

    [Fact]
    public async Task Start_sends_WORKSPACE_WATCHING_with_folder_and_current_word()
    {
        var state = await _session.StartAsync(_webSocket, WatchMessage(Anio), CancellationToken.None);

        var frame = await _webSocket.NextFrameAsync();
        Assert.Equal(SessionState.WatchingWorkspace, state);
        Assert.Equal(MessageType.WorkspaceWatching, frame.GetProperty("type").GetString());
        Assert.Equal(_requestId, frame.GetProperty("requestId").GetString());
        Assert.EndsWith(@"\2026\0000121977", frame.GetProperty("folder").GetString());
        Assert.True(Path.IsPathRooted(frame.GetProperty("folder").GetString()));
        Assert.Equal("2026-10-09T07:51:23", frame.GetProperty("latestWord").GetProperty("changedAt").GetString());
        Assert.True(_factory.Watcher.Started);
    }

    [Fact]
    public async Task Changes_are_pushed_as_WORKSPACE_CHANGED_with_same_requestId()
    {
        await _session.StartAsync(_webSocket, WatchMessage(Anio), CancellationToken.None);
        await _webSocket.NextFrameAsync();

        var saved = CurrentWord with { FileName = "Oficio_20261009-080000.docx", ChangedAt = new DateTime(2026, 10, 9, 8, 0, 0) };
        await _factory.Watcher.PublishAsync(saved);
        var changed = await _webSocket.NextFrameAsync();

        await _factory.Watcher.PublishAsync(null);
        var removed = await _webSocket.NextFrameAsync();

        Assert.Equal(MessageType.WorkspaceChanged, changed.GetProperty("type").GetString());
        Assert.Equal(_requestId, changed.GetProperty("requestId").GetString());
        Assert.Equal("Oficio_20261009-080000.docx", changed.GetProperty("latestWord").GetProperty("filename").GetString());
        Assert.Equal("2026-10-09T08:00:00", changed.GetProperty("latestWord").GetProperty("changedAt").GetString());
        Assert.Equal(MessageType.WorkspaceChanged, removed.GetProperty("type").GetString());
        Assert.Equal(_requestId, removed.GetProperty("requestId").GetString());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("latestWord").ValueKind);
    }

    [Fact]
    public async Task Stop_closes_with_1000_and_disposes_watcher()
    {
        await _session.StartAsync(_webSocket, WatchMessage(Anio), CancellationToken.None);
        await _webSocket.NextFrameAsync();

        var state = await _session.StopAsync(_webSocket, new StopWatchMessage(MessageType.StopWatch, _requestId), CancellationToken.None);
        await _factory.Watcher.PublishAsync(CurrentWord with { Size = 4096 });

        Assert.Equal(SessionState.Closed, state);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, _webSocket.CloseStatus);
        Assert.True(_factory.Watcher.Disposed);
        Assert.Single(_webSocket.SentFrames);
    }

    [Fact]
    public async Task Invalid_remito_key_sends_error_and_closes()
    {
        var state = await _session.StartAsync(_webSocket, WatchMessage("26"), CancellationToken.None);

        var frame = await _webSocket.NextFrameAsync();
        Assert.Equal(SessionState.Closed, state);
        Assert.Equal(MessageType.Error, frame.GetProperty("type").GetString());
        Assert.Equal(ErrorCatalog.WorkspaceInvalidKey, frame.GetProperty("code").GetString());
        Assert.NotNull(_webSocket.CloseStatus);
        Assert.False(_factory.Created);
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private WatchWorkspaceMessage WatchMessage(string anio) =>
        new(MessageType.WatchWorkspace, _requestId, anio, NumeroEmision);

    private sealed class FakeWatcherFactory(WordFileInfo? current) : IWorkspaceWatcherFactory
    {
        public FakeWatcher Watcher { get; } = new(current);

        public bool Created { get; private set; }

        public IWorkspaceWatcher Create(string directory)
        {
            Created = true;
            return Watcher;
        }
    }

    private sealed class FakeWatcher(WordFileInfo? current) : IWorkspaceWatcher
    {
        private readonly Channel<WordFileInfo?> _changes = Channel.CreateUnbounded<WordFileInfo?>();

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public WordFileInfo? Current { get; } = current;

        public ChannelReader<WordFileInfo?> Changes => _changes.Reader;

        public void Start(CancellationToken ct) => Started = true;

        public ValueTask PublishAsync(WordFileInfo? snapshot)
        {
            _changes.Writer.TryWrite(snapshot);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _changes.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
