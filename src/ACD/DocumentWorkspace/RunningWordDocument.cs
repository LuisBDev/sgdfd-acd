using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ACD.DocumentWorkspace;

public static class RunningWordDocument
{
    public static object? Find(string fullPath)
    {
        IRunningObjectTable? table = null;
        IBindCtx? context = null;
        IEnumMoniker? entries = null;
        try
        {
            GetRunningObjectTable(0, out table);
            CreateBindCtx(0, out context);
            table.EnumRunning(out entries);
            var monikers = new IMoniker[1];
            while (entries.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    moniker.GetDisplayName(context, null, out var displayName);
                    if (!MatchesPath(displayName, fullPath)) continue;
                    if (table.GetObject(moniker, out var document) == 0)
                        return document;
                }
                catch (COMException)
                {
                    // A ROT entry can disappear while enumerating other applications.
                }
                finally
                {
                    Release(moniker);
                }
            }
            return null;
        }
        finally
        {
            Release(entries);
            Release(context);
            Release(table);
        }
    }

    private static bool MatchesPath(string name, string fullPath)
    {
        var candidate = name.TrimStart('!');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
            candidate = uri.LocalPath;
        return Path.IsPathFullyQualified(candidate)
            && string.Equals(candidate, Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void GetRunningObjectTable(uint reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CreateBindCtx(uint reserved, out IBindCtx context);
}
