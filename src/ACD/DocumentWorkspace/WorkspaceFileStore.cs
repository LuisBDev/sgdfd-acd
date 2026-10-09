using System.Security.Cryptography;

namespace ACD.DocumentWorkspace;

public sealed record WordFileInfo(string FileName, DateTime LastWriteTime, long Size, string Sha256);

public sealed record WorkspaceStatus(bool FolderExists, WordFileInfo? LatestWord);

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

    public WorkspaceStatus GetStatus(string directory)
    {
        if (!Directory.Exists(directory))
            return new WorkspaceStatus(false, null);

        try
        {
            return new WorkspaceStatus(true, DescribeLatestWord(directory));
        }
        catch (FileNotFoundException)
        {
            return new WorkspaceStatus(true, DescribeLatestWord(directory));
        }
    }

    private static WordFileInfo? DescribeLatestWord(string directory)
    {
        var latestWord = WorkspaceFileNames.FindLatestWord(directory);
        if (latestWord is null)
            return null;

        using var stream = OpenShared(latestWord);
        var size = stream.Length;
        var lastWriteTime = File.GetLastWriteTime(stream.SafeFileHandle);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new WordFileInfo(Path.GetFileName(latestWord), lastWriteTime, size, sha256);
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

        return Describe(path, content);
    }

    public async Task<(WordFileInfo Info, byte[] Content)> ReadWordAsync(string directory, string fileName, CancellationToken ct)
    {
        var path = Path.Combine(directory, fileName);
        await using var stream = OpenShared(path);
        if (stream.Length > maxFileBytes)
            throw new WorkspaceFileTooLargeException(fileName, maxFileBytes);

        var content = new byte[stream.Length];
        await stream.ReadExactlyAsync(content, ct).ConfigureAwait(false);
        return (Describe(path, content), content);
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

    private static WordFileInfo Describe(string path, byte[] content) =>
        new(
            Path.GetFileName(path),
            File.GetLastWriteTime(path),
            content.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(content)));

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
