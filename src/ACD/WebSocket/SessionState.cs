namespace ACD.WebSocket;

public enum SessionState
{
    Idle,
    Connected,
    Authenticated,
    ReceivingFile,
    ReceivingPdfToOpen,
    ReceivingDocumentToEdit,
    EditingDocument,
    WatchingFirma,
    SendingFile,
    Closed
}
