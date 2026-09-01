namespace ACD.WebSocket;

public enum SessionState
{
    Idle,
    Connected,
    Authenticated,
    ReceivingFile,
    ReceivingPdfToOpen,
    WatchingFirma,
    SendingFile,
    Closed
}
