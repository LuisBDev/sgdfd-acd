using System.Security.Cryptography;
using ACD.Configuration;
using ACD.DocumentWorkspace;
using ACD.Files;
using ACD.WebSocket;
using ACD.WebSocket.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACD.Tests.DocumentWorkspace;

public sealed class DocumentWorkspaceRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acd-restore-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceFileStore _store = new(1024);
    private readonly FakeCloser _closer = new();
    private readonly RecordingShell _shell = new();
    private string RemitoDirectory => Path.Combine(_root, "2026", "0000121977");
    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private DocumentWorkspaceHandler Handler() => new(
        new DocumentWorkspaceOptions { RootDirectory = _root, MaxFileBytes = 1024 },
        new WorkspacePaths(new DocumentWorkspaceOptions { RootDirectory = _root }),
        _store, _shell, new UnusedConversion(), _closer, NullLogger.Instance, "test-restore");

    private static RestoreWordMessage Message(string? filename = "OFICIO.docx", string? expectedHash = null) =>
        new(MessageType.RestoreWord, Guid.NewGuid().ToString(), "2026", "0000121977", filename, 2, Hash([8, 9]), expectedHash ?? Hash([1, 2]), true);

    [Fact]
    public async Task Restore_responds_only_after_validated_content_replaces_word()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        using var ws = new RecordingWebSocket();
        var handler = Handler();
        var request = Message();
        Assert.Equal(SessionState.ReceivingWorkspaceFile, await handler.PrepareRestoreWordAsync(ws, request, CancellationToken.None));
        Assert.Empty(ws.SentFrames);
        Assert.Equal(SessionState.Closed, await handler.HandleBinaryFrameAsync(ws, [8, 9], CancellationToken.None));
        var response = await ws.NextFrameAsync();
        Assert.Equal(MessageType.WordRestored, response.GetProperty("type").GetString());
        Assert.Equal(request.RequestId, response.GetProperty("requestId").GetString());
        Assert.True(response.GetProperty("opened").GetBoolean());
        Assert.Equal([Path.Combine(RemitoDirectory, "OFICIO.docx")], _shell.Opened);
        Assert.Equal(new byte[] { 8, 9 }, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
    }

    [Theory]
    [InlineData("../OFICIO.docx", null)]
    [InlineData("OFICIO.docx", "invalid-hash")]
    public async Task Restore_rejects_invalid_command_without_writing(string filename, string? expectedHash)
    {
        using var ws = new RecordingWebSocket();
        var result = await Handler().PrepareRestoreWordAsync(ws, Message(filename, expectedHash), CancellationToken.None);
        Assert.Equal(SessionState.Closed, result);
        Assert.Equal("ERROR", (await ws.NextFrameAsync()).GetProperty("type").GetString());
        Assert.False(Directory.Exists(RemitoDirectory));
    }

    [Fact]
    public async Task Restore_rejects_corrupt_binary_and_preserves_original()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        using var ws = new RecordingWebSocket();
        var handler = Handler();
        await handler.PrepareRestoreWordAsync(ws, Message(), CancellationToken.None);
        await handler.HandleBinaryFrameAsync(ws, [7, 7], CancellationToken.None);
        Assert.Equal(ErrorCatalog.WorkspaceIntegrity, (await ws.NextFrameAsync()).GetProperty("code").GetString());
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
    }

    [Fact]
    public async Task Restore_returns_file_locked_without_success()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        using var ws = new RecordingWebSocket();
        var handler = Handler();
        await handler.PrepareRestoreWordAsync(ws, Message(), CancellationToken.None);
        await using var locked = new FileStream(Path.Combine(RemitoDirectory, "OFICIO.docx"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        await handler.HandleBinaryFrameAsync(ws, [8, 9], CancellationToken.None);
        Assert.Equal(ErrorCatalog.FileLocked, (await ws.NextFrameAsync()).GetProperty("code").GetString());
        Assert.Single(ws.SentFrames);
    }

    [Fact]
    public async Task Restore_returns_changed_error_when_local_hash_changed()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [3, 4], CancellationToken.None);
        using var ws = new RecordingWebSocket();
        var handler = Handler();
        await handler.PrepareRestoreWordAsync(ws, Message(), CancellationToken.None);
        await handler.HandleBinaryFrameAsync(ws, [8, 9], CancellationToken.None);
        Assert.Equal(ErrorCatalog.WorkspaceFileChanged, (await ws.NextFrameAsync()).GetProperty("code").GetString());
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
    }

    [Fact]
    public async Task Restore_closes_only_confirmed_document_before_replacement_then_reopens_it()
    {
        var path = Path.Combine(RemitoDirectory, "OFICIO.docx");
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        using var openWord = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        _closer.OnClose = target =>
        {
            Assert.Equal(path, target);
            Assert.Empty(_shell.Opened);
            openWord.Dispose();
        };
        using var ws = new RecordingWebSocket();
        var handler = Handler();
        await handler.PrepareRestoreWordAsync(ws, Message(), CancellationToken.None);
        await handler.HandleBinaryFrameAsync(ws, [8, 9], CancellationToken.None);
        Assert.Equal(new byte[] { 8, 9 }, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], _closer.Closed);
        Assert.Equal([path], _shell.Opened);
        Assert.Equal(MessageType.WordRestored, (await ws.NextFrameAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Restore_stale_confirmation_does_not_close_word()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [3, 4], CancellationToken.None);
        using var ws = new RecordingWebSocket();
        var handler = Handler();
        await handler.PrepareRestoreWordAsync(ws, Message(), CancellationToken.None);
        await handler.HandleBinaryFrameAsync(ws, [8, 9], CancellationToken.None);
        Assert.Empty(_closer.Closed);
        Assert.Empty(_shell.Opened);
        Assert.Equal(ErrorCatalog.WorkspaceFileChanged, (await ws.NextFrameAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Restore_reopens_original_if_file_changes_after_word_is_closed()
    {
        var path = Path.Combine(RemitoDirectory, "OFICIO.docx");
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        _closer.OnClose = _ => File.WriteAllBytes(path, [4, 5]);
        using var ws = new RecordingWebSocket();
        var handler = Handler();
        await handler.PrepareRestoreWordAsync(ws, Message(), CancellationToken.None);
        await handler.HandleBinaryFrameAsync(ws, [8, 9], CancellationToken.None);
        Assert.Equal(ErrorCatalog.WorkspaceFileChanged, (await ws.NextFrameAsync()).GetProperty("code").GetString());
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], _shell.Opened);
    }

    [Fact]
    public async Task Restore_without_explicit_close_flag_never_closes_word()
    {
        var path = Path.Combine(RemitoDirectory, "OFICIO.docx");
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        _closer.OnClose = _ => throw new InvalidOperationException("Close was not authorized by the command");
        using var ws = new RecordingWebSocket();
        var handler = Handler();
        await handler.PrepareRestoreWordAsync(ws, Message() with { CloseWithoutSaving = false }, CancellationToken.None);
        await handler.HandleBinaryFrameAsync(ws, [8, 9], CancellationToken.None);
        Assert.Empty(_closer.Closed);
        Assert.Empty(_shell.Opened);
        Assert.Equal(ErrorCatalog.FileLocked, (await ws.NextFrameAsync()).GetProperty("code").GetString());
    }

    private static DiscardWordMessage DiscardMessage(string filename = "OFICIO.docx") =>
        new(MessageType.DiscardWord, Guid.NewGuid().ToString(), "2026", "0000121977", filename, Hash([1, 2]), true);

    [Fact]
    public async Task Discard_closes_and_deletes_only_target_without_opening_another_word()
    {
        var path = Path.Combine(RemitoDirectory, "OFICIO.docx");
        Directory.CreateDirectory(RemitoDirectory);
        var other = Path.Combine(RemitoDirectory, "ANTERIOR.docx");
        await File.WriteAllBytesAsync(other, [7]);
        File.SetCreationTime(other, DateTime.Now.AddMinutes(-5));
        File.SetLastWriteTime(other, DateTime.Now.AddMinutes(-5));
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        using var openWord = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        _closer.OnClose = target => { Assert.Equal(path, target); openWord.Dispose(); };
        using var ws = new RecordingWebSocket();
        var request = DiscardMessage();
        Assert.Equal(SessionState.Closed, await Handler().DiscardWordAsync(ws, request, CancellationToken.None));
        var response = await ws.NextFrameAsync();
        Assert.Equal(MessageType.WordDiscarded, response.GetProperty("type").GetString());
        Assert.Equal(request.RequestId, response.GetProperty("requestId").GetString());
        Assert.Equal("OFICIO.docx", response.GetProperty("filename").GetString());
        Assert.False(File.Exists(path));
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(other));
        Assert.Empty(_shell.Opened);
        Assert.Equal([path], _closer.Closed);
    }

    [Theory]
    [InlineData("../OFICIO.docx", true)]
    [InlineData("OFICIO.docx", false)]
    public async Task Discard_rejects_invalid_path_or_missing_confirmation_without_closing(string filename, bool confirmed)
    {
        using var ws = new RecordingWebSocket();
        await Handler().DiscardWordAsync(ws, DiscardMessage(filename) with { CloseWithoutSaving = confirmed }, CancellationToken.None);
        Assert.Equal("ERROR", (await ws.NextFrameAsync()).GetProperty("type").GetString());
        Assert.Empty(_closer.Closed);
        Assert.False(Directory.Exists(RemitoDirectory));
    }

    [Fact]
    public async Task Discard_does_not_close_or_delete_a_changed_word()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [3, 4], CancellationToken.None);
        using var ws = new RecordingWebSocket();
        await Handler().DiscardWordAsync(ws, DiscardMessage(), CancellationToken.None);
        Assert.Equal(ErrorCatalog.WorkspaceFileChanged, (await ws.NextFrameAsync()).GetProperty("code").GetString());
        Assert.Empty(_closer.Closed);
        Assert.True(File.Exists(Path.Combine(RemitoDirectory, "OFICIO.docx")));
    }

    [Fact]
    public async Task Discard_reopens_original_if_it_changes_after_closing()
    {
        var path = Path.Combine(RemitoDirectory, "OFICIO.docx");
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        _closer.OnClose = _ => File.WriteAllBytes(path, [3, 4]);
        using var ws = new RecordingWebSocket();
        await Handler().DiscardWordAsync(ws, DiscardMessage(), CancellationToken.None);
        Assert.Equal(ErrorCatalog.WorkspaceFileChanged, (await ws.NextFrameAsync()).GetProperty("code").GetString());
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], _shell.Opened);
    }

    private sealed class FakeCloser : IWordDocumentCloser
    {
        public Action<string>? OnClose { get; set; }
        public List<string> Closed { get; } = [];
        public Task<bool> CloseWithoutSavingAsync(string fullPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (OnClose is null) return Task.FromResult(false);
            OnClose(fullPath);
            Closed.Add(fullPath);
            return Task.FromResult(true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingShell : IShellLauncher
    {
        public List<string> Opened { get; } = [];
        public void Open(string filePath) => Opened.Add(filePath);
        public void OpenFolder(string directory) => throw new InvalidOperationException();
    }

    private sealed class UnusedConversion : IConversionService
    {
        public Task<byte[]> ConvertDocxToPdfAsync(byte[] docx, CancellationToken ct) => throw new InvalidOperationException();
    }
}
