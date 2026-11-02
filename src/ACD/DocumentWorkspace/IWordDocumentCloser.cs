namespace ACD.DocumentWorkspace;

public interface IWordDocumentCloser
{
    Task<bool> CloseWithoutSavingAsync(string fullPath, CancellationToken ct);
}
