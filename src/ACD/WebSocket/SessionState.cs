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
    SendingFile,
    Closed
}
