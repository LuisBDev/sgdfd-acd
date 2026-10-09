namespace ACD.WebSocket;

public enum SessionState
{
    Idle,
    Connected,
    Authenticated,
    ReceivingFile,
    ReceivingPdfToOpen,
    ReceivingWorkspaceFile,
    WatchingFirma,
    WatchingWorkspace,
    SendingFile,
    Closed
}
