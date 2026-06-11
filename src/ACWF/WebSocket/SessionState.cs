namespace ACWF.WebSocket;

public enum SessionState
{
    Idle,
    Connected,
    Authenticated,
    ReceivingFile,
    WatchingFirma,
    SendingFile,
    Closed
}
