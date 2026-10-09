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
    ReceivingWorkspaceFile,
    WatchingFirma,
    SendingFile,
    Closed
}
