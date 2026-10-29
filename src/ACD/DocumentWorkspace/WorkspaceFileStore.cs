using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ACD.DocumentWorkspace;

public sealed record WordFileInfo(string FileName, DateTime ChangedAt, long Size, string Sha256);

public sealed class WorkspaceFileExistsException(string fileName)
    : IOException($"The file {fileName} already exists in the remito folder");

public sealed class WorkspaceFileTooLargeException(string fileName, long maxFileBytes)
    : IOException($"The file {fileName} exceeds the {maxFileBytes} bytes limit");

public sealed class WorkspaceFileChangedException(string fileName)
    : IOException($"The file {fileName} changed since the discard was confirmed");

public sealed class WorkspaceFileLockedException(string fileName)
    : IOException($"Close {fileName} in Word before restoring it");

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

    public void ValidateRestoreTarget(string directory, string fileName, string expectedSha256)
    {
        try
        {
            var latest = DescribeLatestWord(directory) ?? throw new FileNotFoundException("The Word no longer exists");
            if (!string.Equals(latest.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(latest.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceFileChangedException(fileName);
        }
        catch (IOException ex) when (IsLocked(ex))
        {
            throw new WorkspaceFileLockedException(fileName);
        }
    }

    public async Task DiscardWordAsync(string directory, string fileName, string expectedSha256, CancellationToken ct)
    {
        var path = Path.Combine(directory, fileName);
        if (File.Exists(Path.Combine(directory, "~$" + fileName))
            || File.Exists(Path.Combine(directory, "~$" + fileName[2..])))
            throw new WorkspaceFileLockedException(fileName);
        try
        {
            await using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            ValidateRestoreTarget(directory, fileName, expectedSha256);
            var currentHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(current, ct));
            if (!string.Equals(currentHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceFileChangedException(fileName);
            ct.ThrowIfCancellationRequested();
            File.Delete(path);
        }
        catch (IOException ex) when (IsLocked(ex))
        {
            throw new WorkspaceFileLockedException(fileName);
        }
    }

    public async Task<WordFileInfo> RestoreWordAsync(
        string directory, string fileName, byte[] content, string expectedSha256, CancellationToken ct)
    {
        if (content.LongLength > maxFileBytes)
            throw new WorkspaceFileTooLargeException(fileName, maxFileBytes);
        var path = Path.Combine(directory, fileName);
        var temporary = Path.Combine(directory, $".acd-restore-{Guid.NewGuid():N}.tmp");
        if (File.Exists(Path.Combine(directory, "~$" + fileName))
            || File.Exists(Path.Combine(directory, "~$" + fileName[2..])))
            throw new WorkspaceFileLockedException(fileName);
        try
        {
            // Block writers while checking and atomically replacing the confirmed file.
            await using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var latest = DescribeLatestWord(directory);
            if (latest is null || !string.Equals(latest.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceFileChangedException(fileName);
            var currentHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(current, ct));
            if (!string.Equals(currentHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceFileChangedException(fileName);
            await File.WriteAllBytesAsync(temporary, content, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Replace(temporary, path, null);
            return Describe(path, EffectiveChangedAt(path), content);
        }
        catch (IOException ex) when (IsLocked(ex))
        {
            throw new WorkspaceFileLockedException(fileName);
        }
        finally
        {
            DeleteBestEffort(temporary);
        }
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
