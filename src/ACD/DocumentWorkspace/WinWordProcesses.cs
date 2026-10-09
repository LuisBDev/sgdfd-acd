using System.ComponentModel;
using System.Diagnostics;

namespace ACD.DocumentWorkspace;

public sealed class WinWordProcesses : IProcessKiller
{
    private const string ProcessName = "WINWORD";

    public IReadOnlySet<int> CurrentIds()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            return processes.Select(process => process.Id).ToHashSet();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
                process.Kill(true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }
}
