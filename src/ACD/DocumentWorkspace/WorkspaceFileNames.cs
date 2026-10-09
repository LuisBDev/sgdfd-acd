using ACD.Files;

namespace ACD.DocumentWorkspace;

public static class WorkspaceFileNames
{
    private const string WordExtension = ".docx";
    private const string PdfExtension = ".pdf";
    private const string LockFilePrefix = "~$";

    public static bool IsValidFileName(string name, string requiredExtension) =>
        SafeFileName.IsValid(name, extension => string.Equals(extension, requiredExtension, StringComparison.OrdinalIgnoreCase));

    public static string? FindLatestWord(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        return new DirectoryInfo(directory)
            .EnumerateFiles("*" + WordExtension)
            .Where(file => string.Equals(file.Extension, WordExtension, StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.StartsWith(LockFilePrefix, StringComparison.Ordinal))
            .OrderByDescending(EffectiveChangedAt)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    public static DateTime EffectiveChangedAt(FileInfo file) =>
        file.CreationTime > file.LastWriteTime ? file.CreationTime : file.LastWriteTime;

    public static string PdfCopyName(string wordFileName) =>
        Path.GetFileNameWithoutExtension(wordFileName) + PdfExtension;

    public static string AlternatePdfCopyName(string wordFileName) =>
        Path.GetFileNameWithoutExtension(wordFileName) + " (2)" + PdfExtension;
}
