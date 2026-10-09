using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ACD.DocumentWorkspace;

public sealed record WordFileInfo(string FileName, DateTime ChangedAt, long Size, string Sha256);

public sealed class WorkspaceFileExistsException(string fileName)
    : IOException($"The file {fileName} already exists in the remito folder");

public sealed class WorkspaceFileTooLargeException(string fileName, long maxFileBytes)
    : IOException($"The file {fileName} exceeds the {maxFileBytes} bytes limit");

public sealed class WorkspaceFileStore(long maxFileBytes)
{
    private const int FileExistsHResult = unchecked((int)0x80070050);
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult = unchecked((int)0x80070021);
    private const FileShare ReadWhileOpenInWord = FileShare.ReadWrite | FileShare.Delete;

    public WordFileInfo? DescribeLatestWord(string directory)
    {
        try
        {
            return DescribeWord(WorkspaceFileNames.FindLatestWord(directory));
        }
        catch (FileNotFoundException)
        {
            return DescribeWord(WorkspaceFileNames.FindLatestWord(directory));
        }
    }

    private static WordFileInfo? DescribeWord(string? path)
    {
        if (path is null)
            return null;

        using var stream = OpenShared(path);
        var size = stream.Length;
        var changedAt = EffectiveChangedAt(stream.SafeFileHandle);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new WordFileInfo(Path.GetFileName(path), changedAt, size, sha256);
    }

    public async Task<WordFileInfo> WriteWordAsync(string directory, string fileName, byte[] content, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException ex) when (ex.HResult == FileExistsHResult || File.Exists(path))
        {
            throw new WorkspaceFileExistsException(fileName);
        }

        try
        {
            await using (stream)
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
        }
        catch
        {
            DeleteBestEffort(path);
            throw;
        }

        return Describe(path, EffectiveChangedAt(path), content);
    }

    public async Task<(WordFileInfo Info, byte[] Content)> ReadWordAsync(string directory, string fileName, CancellationToken ct)
    {
        var path = Path.Combine(directory, fileName);
        await using var stream = OpenShared(path);
        if (stream.Length > maxFileBytes)
            throw new WorkspaceFileTooLargeException(fileName, maxFileBytes);

        var content = new byte[stream.Length];
        await stream.ReadExactlyAsync(content, ct).ConfigureAwait(false);
        return (Describe(path, EffectiveChangedAt(stream.SafeFileHandle), content), content);
    }

    public async Task<(string FileName, bool Renamed)> WritePdfCopyAsync(string directory, string wordFileName, byte[] pdf, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var fileName = WorkspaceFileNames.PdfCopyName(wordFileName);
        try
        {
            await OverwriteAsync(Path.Combine(directory, fileName), pdf, ct).ConfigureAwait(false);
            return (fileName, false);
        }
        catch (IOException ex) when (IsLocked(ex))
        {
            var alternateName = WorkspaceFileNames.AlternatePdfCopyName(wordFileName);
            await OverwriteAsync(Path.Combine(directory, alternateName), pdf, ct).ConfigureAwait(false);
            return (alternateName, true);
        }
    }

    private static async Task OverwriteAsync(string path, byte[] content, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(content, ct).ConfigureAwait(false);
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, ReadWhileOpenInWord);

    private static WordFileInfo Describe(string path, DateTime changedAt, byte[] content) =>
        new(
            Path.GetFileName(path),
            changedAt,
            content.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(content)));

    private static DateTime EffectiveChangedAt(string path) =>
        WorkspaceFileNames.EffectiveChangedAt(new FileInfo(path));

    private static DateTime EffectiveChangedAt(SafeFileHandle handle) =>
        WorkspaceFileNames.EffectiveChangedAt(File.GetCreationTime(handle), File.GetLastWriteTime(handle));

    private static bool IsLocked(IOException ex) =>
        ex.HResult is SharingViolationHResult or LockViolationHResult;

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
