using System.Security.Cryptography;
using ACD.DocumentWorkspace;

namespace ACD.Tests.DocumentWorkspace;

public sealed class WorkspaceFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acd-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceFileStore _store = new();

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
    public void GetStatus_devuelveHashYFechaDelMasReciente()
    {
        Directory.CreateDirectory(RemitoDirectory);
        var lastWriteTime = new DateTime(2026, 10, 9, 10, 30, 15, DateTimeKind.Local);
        CreateFile("anterior.docx", [1], lastWriteTime.AddMinutes(-5));
        CreateFile("reciente.docx", [2, 3], lastWriteTime);

        var status = _store.GetStatus(RemitoDirectory);

        Assert.True(status.FolderExists);
        Assert.NotNull(status.LatestWord);
        Assert.Equal("reciente.docx", status.LatestWord.FileName);
        Assert.Equal(lastWriteTime, status.LatestWord.LastWriteTime);
        Assert.Equal(2, status.LatestWord.Size);
        Assert.Equal(Sha256Of([2, 3]), status.LatestWord.Sha256);
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

    private void CreateFile(string name, byte[] content, DateTime lastWriteTime)
    {
        var path = Path.Combine(RemitoDirectory, name);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTime(path, lastWriteTime);
    }

    private static string Sha256Of(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
