using System.Security.Cryptography;
using ACD.DocumentWorkspace;

namespace ACD.Tests.DocumentWorkspace;

public sealed class WorkspaceFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acd-tests-" + Guid.NewGuid().ToString("N"));
    private const long MaxFileBytes = 16;
    private readonly WorkspaceFileStore _store = new(MaxFileBytes);

    private string RemitoDirectory => Path.Combine(_root, "2026", "0000121972");

    [Fact]
    public async Task WriteWord_creaCarpetaYArchivo_yFallaSiExiste()
    {
        byte[] content = [1, 2, 3];

        var info = await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", content, CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
        Assert.Equal(Sha256Of(content), info.Sha256);
        await Assert.ThrowsAsync<WorkspaceFileExistsException>(
            () => _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [9], CancellationToken.None));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
    }

    [Fact]
    public async Task ReadWord_conArchivoAbiertoConLockDeEscritura_leeIgual()
    {
        byte[] content = [4, 5, 6, 7];
        Directory.CreateDirectory(RemitoDirectory);
        var path = Path.Combine(RemitoDirectory, "OFICIO.docx");
        await File.WriteAllBytesAsync(path, content);

        await using var wordHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var (info, read) = await _store.ReadWordAsync(RemitoDirectory, "OFICIO.docx", CancellationToken.None);

        Assert.Equal(content, read);
        Assert.Equal(Sha256Of(content), info.Sha256);
        Assert.Equal(content.LongLength, info.Size);
    }

    [Fact]
    public void DescribeLatestWord_devuelveHashYFechaEfectivaDelMasReciente()
    {
        Directory.CreateDirectory(RemitoDirectory);
        var changedAt = new DateTime(2026, 10, 9, 10, 30, 15, DateTimeKind.Local);
        CreateFile("anterior.docx", [1], changedAt.AddMinutes(-5));
        CreateFile("reciente.docx", [2, 3], changedAt.AddMinutes(-20));
        File.SetCreationTime(Path.Combine(RemitoDirectory, "reciente.docx"), changedAt);

        var latestWord = _store.DescribeLatestWord(RemitoDirectory);

        Assert.NotNull(latestWord);
        Assert.Equal("reciente.docx", latestWord.FileName);
        Assert.Equal(changedAt, latestWord.ChangedAt);
        Assert.Equal(2, latestWord.Size);
        Assert.Equal(Sha256Of([2, 3]), latestWord.Sha256);
    }

    [Fact]
    public async Task WritePdfCopy_siElPdfEstaBloqueado_escribeConSufijo2()
    {
        Directory.CreateDirectory(RemitoDirectory);
        CreateFile("OFICIO.pdf", [0], DateTime.Now);
        byte[] pdf = [8, 9];

        (string FileName, bool Renamed) result;
        await using (new FileStream(Path.Combine(RemitoDirectory, "OFICIO.pdf"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await _store.WritePdfCopyAsync(RemitoDirectory, "OFICIO.docx", pdf, CancellationToken.None);
        }

        Assert.Equal(("OFICIO (2).pdf", true), result);
        Assert.Equal(pdf, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO (2).pdf")));

        var overwritten = await _store.WritePdfCopyAsync(RemitoDirectory, "OFICIO.docx", pdf, CancellationToken.None);

        Assert.Equal(("OFICIO.pdf", false), overwritten);
        Assert.Equal(pdf, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.pdf")));
    }

    [Fact]
    public async Task ReadWord_conArchivoMayorAlLimite_rechazaSinLeer()
    {
        Directory.CreateDirectory(RemitoDirectory);
        CreateFile("OFICIO.docx", new byte[MaxFileBytes + 1], DateTime.Now);

        await Assert.ThrowsAsync<WorkspaceFileTooLargeException>(
            () => _store.ReadWordAsync(RemitoDirectory, "OFICIO.docx", CancellationToken.None));
    }

    [Fact]
    public async Task RestoreWord_replaces_same_file_with_saved_content_without_creating_another_word()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2, 3], CancellationToken.None);
        var info = await _store.RestoreWordAsync(RemitoDirectory, "OFICIO.docx", [8, 9], Sha256Of([1, 2, 3]), CancellationToken.None);
        Assert.Equal(new byte[] { 8, 9 }, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
        Assert.Equal(Sha256Of([8, 9]), info.Sha256);
        Assert.Equal("OFICIO.docx", info.FileName);
        Assert.Single(Directory.GetFiles(RemitoDirectory));
        Assert.Equal(info, _store.DescribeLatestWord(RemitoDirectory));
    }

    [Fact]
    public async Task RestoreWord_rejects_concurrent_edit_and_preserves_content()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [4, 5], CancellationToken.None);
        await Assert.ThrowsAsync<WorkspaceFileChangedException>(() =>
            _store.RestoreWordAsync(RemitoDirectory, "OFICIO.docx", [9], Sha256Of([1, 2]), CancellationToken.None));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
        Assert.Single(Directory.GetFiles(RemitoDirectory));
    }

    [Fact]
    public async Task RestoreWord_rejects_locked_file_and_preserves_content()
    {
        var path = Path.Combine(RemitoDirectory, "OFICIO.docx");
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [4, 5], CancellationToken.None);
        await using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            await Assert.ThrowsAsync<WorkspaceFileLockedException>(() =>
                _store.RestoreWordAsync(RemitoDirectory, "OFICIO.docx", [9], Sha256Of([4, 5]), CancellationToken.None));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("~$OFICIO.docx")]
    [InlineData("~$ICIO.docx")]
    public async Task RestoreWord_rejects_word_owner_file(string ownerFile)
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [4, 5], CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(RemitoDirectory, ownerFile), [0]);
        await Assert.ThrowsAsync<WorkspaceFileLockedException>(() =>
            _store.RestoreWordAsync(RemitoDirectory, "OFICIO.docx", [9], Sha256Of([4, 5]), CancellationToken.None));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
    }

    [Fact]
    public async Task RestoreWord_does_not_create_missing_file()
    {
        Directory.CreateDirectory(RemitoDirectory);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _store.RestoreWordAsync(RemitoDirectory, "OFICIO.docx", [9], Sha256Of([4, 5]), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(RemitoDirectory));
    }

    [Fact]
    public async Task RestoreWord_cancelled_leaves_original_file_and_no_temporary_files()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [4, 5], CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.RestoreWordAsync(RemitoDirectory, "OFICIO.docx", [9], Sha256Of([4, 5]), cts.Token));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
        Assert.Single(Directory.GetFiles(RemitoDirectory));
    }

    [Fact]
    public async Task RestoreWord_rejects_if_another_word_became_latest()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [4, 5], CancellationToken.None);
        CreateFile("NUEVO.docx", [7], DateTime.Now.AddMinutes(1));
        await Assert.ThrowsAsync<WorkspaceFileChangedException>(() =>
            _store.RestoreWordAsync(RemitoDirectory, "OFICIO.docx", [9], Sha256Of([4, 5]), CancellationToken.None));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "OFICIO.docx")));
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(Path.Combine(RemitoDirectory, "NUEVO.docx")));
    }

    [Fact]
    public async Task DiscardWord_removes_the_confirmed_file_without_creating_replacement_files()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        await _store.DiscardWordAsync(RemitoDirectory, "OFICIO.docx", Sha256Of([1, 2]), CancellationToken.None);
        Assert.Empty(Directory.GetFiles(RemitoDirectory));
    }

    [Fact]
    public async Task DiscardWord_preserves_locked_or_changed_files()
    {
        var path = Path.Combine(RemitoDirectory, "OFICIO.docx");
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        await Assert.ThrowsAsync<WorkspaceFileChangedException>(() =>
            _store.DiscardWordAsync(RemitoDirectory, "OFICIO.docx", Sha256Of([3]), CancellationToken.None));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            await Assert.ThrowsAsync<WorkspaceFileLockedException>(() =>
                _store.DiscardWordAsync(RemitoDirectory, "OFICIO.docx", Sha256Of([1, 2]), CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DiscardWord_cancelled_does_not_remove_the_file()
    {
        await _store.WriteWordAsync(RemitoDirectory, "OFICIO.docx", [1, 2], CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.DiscardWordAsync(RemitoDirectory, "OFICIO.docx", Sha256Of([1, 2]), cts.Token));
        Assert.True(File.Exists(Path.Combine(RemitoDirectory, "OFICIO.docx")));
    }

    private void CreateFile(string name, byte[] content, DateTime changedAt)
    {
        var path = Path.Combine(RemitoDirectory, name);
        File.WriteAllBytes(path, content);
        File.SetCreationTime(path, changedAt);
        File.SetLastWriteTime(path, changedAt);
    }

    private static string Sha256Of(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
