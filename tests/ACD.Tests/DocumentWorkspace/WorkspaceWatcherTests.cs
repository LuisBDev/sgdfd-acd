using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ACD.DocumentWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACD.Tests.DocumentWorkspace;

public sealed partial class WorkspaceWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NextTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(500);
    private static readonly DateTime BaseTime = DateTime.Now.AddHours(-1);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "acd-tests-" + Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(_root, "2026", "0000121972");

    [Fact]
    public async Task Start_creates_missing_folder_and_sets_current_to_latest_word()
    {
        await using (var empty = CreateWatcher())
        {
            empty.Start(CancellationToken.None);

            Assert.True(Directory.Exists(Folder));
            Assert.Null(empty.Current);
        }

        WriteWord("a.docx", [1], BaseTime);
        WriteWord("b.docx", [2], BaseTime.AddMinutes(5));
        await using var watcher = CreateWatcher();

        watcher.Start(CancellationToken.None);

        Assert.Equal("b.docx", watcher.Current?.FileName);
    }

    [Fact]
    public async Task Creating_a_docx_emits_its_snapshot()
    {
        WriteWord("a.docx", [1], BaseTime);
        await using var watcher = StartWatcher();

        await File.WriteAllBytesAsync(PathOf("nuevo.docx"), [7, 7]);

        var snapshot = await NextAsync(watcher);
        Assert.Equal("nuevo.docx", snapshot?.FileName);
        Assert.Matches(Sha256Hex(), snapshot?.Sha256 ?? string.Empty);
    }

    [Fact]
    public async Task Modifying_the_active_word_emits_new_sha()
    {
        WriteWord("a.docx", [1], BaseTime);
        await using var watcher = StartWatcher();
        var initialSha = watcher.Current?.Sha256;

        await File.WriteAllBytesAsync(PathOf("a.docx"), [1, 2, 3]);

        var snapshot = await NextAsync(watcher);
        Assert.Equal("a.docx", snapshot?.FileName);
        Assert.NotEqual(initialSha, snapshot?.Sha256);
    }

    [Fact]
    public async Task Save_via_temp_and_rename_emits_one_final_snapshot_without_null()
    {
        WriteWord("a.docx", [1], BaseTime);
        await using var watcher = StartWatcher();
        byte[] saved = [4, 5, 6];

        await File.WriteAllBytesAsync(PathOf("~WRL0001.tmp"), saved);
        File.Delete(PathOf("a.docx"));
        File.Move(PathOf("~WRL0001.tmp"), PathOf("a.docx"));

        var snapshot = await NextAsync(watcher);
        Assert.Equal("a.docx", snapshot?.FileName);
        Assert.Equal(Sha256Of(saved), snapshot?.Sha256);
        await AssertNoChangeAsync(watcher);
    }

    [Fact]
    public async Task Lock_files_are_ignored()
    {
        WriteWord("a.docx", [1], BaseTime);
        await using var watcher = StartWatcher();

        await File.WriteAllBytesAsync(PathOf("~$a.docx"), [9]);

        await AssertNoChangeAsync(watcher);
    }

    [Fact]
    public async Task Burst_of_writes_within_debounce_emits_once()
    {
        WriteWord("a.docx", [1], BaseTime);
        await using var watcher = StartWatcher();

        for (byte write = 2; write <= 6; write++)
            await File.WriteAllBytesAsync(PathOf("a.docx"), [write, write]);

        var snapshot = await NextAsync(watcher);
        Assert.Equal(Sha256Of([6, 6]), snapshot?.Sha256);
        await AssertNoChangeAsync(watcher);
    }

    [Fact]
    public async Task Unchanged_snapshot_is_not_emitted()
    {
        byte[] content = [1, 2];
        WriteWord("a.docx", content, BaseTime);
        await using var watcher = StartWatcher();

        WriteWord("a.docx", content, BaseTime);

        await AssertNoChangeAsync(watcher);
    }

    [Fact]
    public async Task Deleting_active_word_falls_back_to_previous()
    {
        WriteWord("a.docx", [1], BaseTime);
        WriteWord("b.docx", [2], BaseTime.AddMinutes(5));
        await using var watcher = StartWatcher();

        File.Delete(PathOf("b.docx"));

        var snapshot = await NextAsync(watcher);
        Assert.Equal("a.docx", snapshot?.FileName);
    }

    [Fact]
    public async Task Deleting_last_word_emits_null()
    {
        WriteWord("a.docx", [1], BaseTime);
        await using var watcher = StartWatcher();

        File.Delete(PathOf("a.docx"));

        Assert.Null(await NextAsync(watcher));
    }

    [Fact]
    public async Task Pasted_file_with_old_last_write_and_new_creation_becomes_active()
    {
        WriteWord("a.docx", [1], BaseTime);
        var source = Path.Combine(_root, "origen.docx");
        await File.WriteAllBytesAsync(source, [3, 3]);
        File.SetLastWriteTime(source, BaseTime.AddDays(-1));
        await using var watcher = StartWatcher();

        File.Copy(source, PathOf("pegado.docx"));

        var snapshot = await NextAsync(watcher);
        Assert.Equal("pegado.docx", snapshot?.FileName);
    }

    private WorkspaceWatcher CreateWatcher() =>
        new(
            Folder,
            new WorkspaceFileStore(20 * 1024 * 1024),
            new ImmediateStableFileProbe(),
            Debounce,
            NullLogger<WorkspaceWatcher>.Instance);

    private WorkspaceWatcher StartWatcher()
    {
        var watcher = CreateWatcher();
        watcher.Start(CancellationToken.None);
        return watcher;
    }

    private static async Task<WordFileInfo?> NextAsync(IWorkspaceWatcher watcher)
    {
        using var timeout = new CancellationTokenSource(NextTimeout);
        try
        {
            return await watcher.Changes.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"No llegó ninguna foto en {NextTimeout.TotalSeconds} s");
            throw;
        }
    }

    private static async Task AssertNoChangeAsync(IWorkspaceWatcher watcher)
    {
        await Task.Delay(QuietWindow);
        Assert.False(watcher.Changes.TryRead(out var unexpected), $"Foto inesperada: {unexpected}");
    }

    private void WriteWord(string name, byte[] content, DateTime changedAt)
    {
        Directory.CreateDirectory(Folder);
        var path = PathOf(name);
        File.WriteAllBytes(path, content);
        File.SetCreationTime(path, changedAt);
        File.SetLastWriteTime(path, changedAt);
    }

    private string PathOf(string name) => Path.Combine(Folder, name);

    private static string Sha256Of(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
