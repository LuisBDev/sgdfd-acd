namespace ACD.WebSocket;

public enum SessionOperation
{
    Signing,
    PdfOpen,
    DocumentEdit,
    Workspace
}

public interface ISessionGate
{
    bool IsActive { get; }
    bool IsSigningActive { get; }
    bool IsPdfOpenActive { get; }
    bool IsDocumentEditActive { get; }
    bool IsWorkspaceActive { get; }
    Task<bool> TryAcquireConnectionAsync(CancellationToken ct);
    void ReleaseConnection();
    Task<bool> TryAcquireAsync(SessionOperation operation, CancellationToken ct);
    void Release(SessionOperation operation);
}
