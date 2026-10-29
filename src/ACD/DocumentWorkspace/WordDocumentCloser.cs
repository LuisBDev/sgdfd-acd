using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace ACD.DocumentWorkspace;

public sealed class WordDocumentCloser(Func<string, object?> findDocument) : IWordDocumentCloser
{
    private const int DoNotSaveChanges = 0;

    public async Task<bool> CloseWithoutSavingAsync(string fullPath, CancellationToken ct)
    {
        var closed = false;
        await StaWorker.RunAsync("acd-word-close", () =>
        {
            object? document = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                document = findDocument(fullPath);
                if (document is null) return;
                var actualPath = (string)((dynamic)document).FullName;
                if (!string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase))
                    throw new WorkspaceFileChangedException(Path.GetFileName(fullPath));
                ct.ThrowIfCancellationRequested();
                ((dynamic)document).Close(DoNotSaveChanges);
                closed = true;
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException or RuntimeBinderException)
            {
                throw new WorkspaceFileLockedException(Path.GetFileName(fullPath));
            }
            finally
            {
                if (document is not null && Marshal.IsComObject(document))
                    Marshal.ReleaseComObject(document);
            }
        }).ConfigureAwait(false);
        return closed;
    }
}
