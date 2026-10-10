using System.Diagnostics;

namespace ACD.Files;

public sealed class ShellLauncher : IShellLauncher
{
    public void Open(string filePath)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true,
            Verb = "open"
        });

        if (process is null)
            throw new InvalidOperationException("Windows did not start a process for the file");
    }

    public void OpenFolder(string directory)
    {
        var startInfo = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
        startInfo.ArgumentList.Add(directory);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Windows did not start the file explorer");
    }
}
