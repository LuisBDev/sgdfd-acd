namespace ACD.WebSocket;

public enum SessionState
{
    Idle,
    Connected,
    Authenticated,
    ReceivingFile,
    WatchingFirma,
    SendingFile,
    WaitingCleanupConfirm,
    Closed
}