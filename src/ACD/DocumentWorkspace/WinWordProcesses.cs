using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace ACD.DocumentWorkspace;

public sealed class WinWordProcesses : IWordProcesses
{
    private const string ProcessName = "WINWORD";
    private static readonly string[] AutomationSwitches = ["/Automation", "-Embedding"];

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

    public bool IsAutomationInstance(int processId) =>
        ReadCommandLine(processId) is { } commandLine
            ? AutomationSwitches.Any(flag => commandLine.Contains(flag, StringComparison.OrdinalIgnoreCase))
            : HasNoMainWindow(processId);

    public void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase)
                && IsAutomationInstance(processId))
                process.Kill(true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }

    private static string? ReadCommandLine(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using var results = searcher.Get();
            return results.Cast<ManagementBaseObject>()
                .Select(result => result["CommandLine"] as string)
                .FirstOrDefault(commandLine => !string.IsNullOrWhiteSpace(commandLine));
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasNoMainWindow(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainWindowHandle == IntPtr.Zero;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
