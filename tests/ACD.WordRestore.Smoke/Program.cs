using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ACD.DocumentWorkspace;

namespace ACD.WordRestore.Smoke;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "acd-word-smoke-" + Guid.NewGuid().ToString("N"));
        object? application = null;
        dynamic? target = null;
        dynamic? other = null;
        dynamic? reopened = null;
        var owned = false;
        try
        {
            Directory.CreateDirectory(root);
            var processes = new WinWordProcesses();
            var before = processes.CurrentIds();
            application = WordComConversionService.CreateWordApplication() ?? throw new InvalidOperationException("Word is not installed");
            owned = processes.CurrentIds().Except(before).Any(processes.IsAutomationInstance);
            if (!owned) throw new InvalidOperationException("No separate automation instance was identified; smoke test will not touch this Word instance");
            dynamic word = application;
            word.AutomationSecurity = 3;
            word.Visible = false;
            word.DisplayAlerts = 0;
            var targetPath = Path.Combine(root, "target.docx");
            var otherPath = Path.Combine(root, "other.docx");
            other = word.Documents.Add();
            other.Content.Text = "OTHER SAVED DOCUMENT";
            other.SaveAs2(otherPath);
            target = word.Documents.Add();
            target.Content.Text = "BASE TO RESTORE";
            target.SaveAs2(targetPath);
            var saved = ReadShared(targetPath);
            target.Content.Text = "SAVED CHANGE TO DISCARD";
            target.Save();
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(ReadShared(targetPath)));
            target.Content.Text = "UNSAVED CHANGE TO DISCARD";
            other.Content.Text = "OTHER UNSAVED DOCUMENT MUST SURVIVE";
            var closer = new WordDocumentCloser(RunningWordDocument.Find);
            if (!closer.CloseWithoutSavingAsync(targetPath, CancellationToken.None).GetAwaiter().GetResult())
                throw new InvalidOperationException("The temporary document was not found in the running object table");
            if (!((string)other.Content.Text).Contains("OTHER UNSAVED DOCUMENT MUST SURVIVE", StringComparison.Ordinal))
                throw new InvalidOperationException("The other temporary document was modified");
            var store = new WorkspaceFileStore(20 * 1024 * 1024);
            store.RestoreWordAsync(root, "target.docx", saved, expectedHash, CancellationToken.None).GetAwaiter().GetResult();
            reopened = word.Documents.Open(FileName: targetPath, ConfirmConversions: false, ReadOnly: false, AddToRecentFiles: false);
            if (!((string)reopened.Content.Text).Contains("BASE TO RESTORE", StringComparison.Ordinal))
                throw new InvalidOperationException("Word did not reopen the restored version");
            if ((int)word.Documents.Count != 2)
                throw new InvalidOperationException("Unexpected document count after restoring and reopening");
            Console.WriteLine("PASS: restore closed only the target without saving, restored its saved bytes, reopened it, and preserved the other document.");
            var discardHash = Convert.ToHexStringLower(SHA256.HashData(ReadShared(targetPath)));
            store.ValidateRestoreTarget(root, "target.docx", discardHash);
            reopened.Content.Text = "UNSAVED TEXT BEFORE LOCAL DISCARD";
            if (!closer.CloseWithoutSavingAsync(targetPath, CancellationToken.None).GetAwaiter().GetResult())
                throw new InvalidOperationException("The reopened target was not found for local discard");
            store.DiscardWordAsync(root, "target.docx", discardHash, CancellationToken.None).GetAwaiter().GetResult();
            if (File.Exists(targetPath) || (int)word.Documents.Count != 1)
                throw new InvalidOperationException("Local discard left the target file or opened a replacement");
            if (!((string)other.Content.Text).Contains("OTHER UNSAVED DOCUMENT MUST SURVIVE", StringComparison.Ordinal))
                throw new InvalidOperationException("Local discard affected the other document");
            Console.WriteLine("PASS: local discard closed and deleted only the target, opened no replacement and preserved the other document's unsaved content.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex}");
            return 1;
        }
        finally
        {
            Close(reopened);
            Close(target);
            Close(other);
            if (owned && application is not null)
            {
                try { if ((int)((dynamic)application).Documents.Count == 0) ((dynamic)application).Quit(0); }
                catch (Exception) { }
            }
            Release(reopened);
            Release(target);
            Release(other);
            Release(application);
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Close(dynamic? document)
    {
        if (document is null) return;
        try { document.Close(0); }
        catch (Exception) { }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
