using System.Diagnostics;

namespace ACD.PdfOpen;

public sealed class ShellPdfLauncher : IPdfLauncher
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
            throw new InvalidOperationException("Windows did not start a process for the PDF file");
    }
}
