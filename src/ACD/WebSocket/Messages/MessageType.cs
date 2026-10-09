namespace ACD.WebSocket.Messages;

public static class MessageType
{
    // Incoming (MFD → ACD)
    public const string Auth = "AUTH";
    public const string PdfDownload = "PDF_DOWNLOAD";
    public const string OpenPdf = "OPEN_PDF";
    public const string RequestSignedFile = "REQUEST_SIGNED_FILE";
    public const string WriteWord = "WRITE_WORD";
    public const string ReadWord = "READ_WORD";
    public const string WritePdfCopy = "WRITE_PDF_COPY";
    public const string OpenFolder = "OPEN_FOLDER";
    public const string ConvertToPdf = "CONVERT_TO_PDF";
    public const string WatchWorkspace = "WATCH_WORKSPACE";
    public const string StopWatch = "STOP_WATCH";

    // Outgoing (ACD → MFD)
    public const string AuthOk = "AUTH_OK";
    public const string Connected = "CONNECTED";
    public const string PdfReceived = "PDF_RECEIVED";
    public const string PdfOpened = "PDF_OPENED";
    public const string FirmaDisponible = "FIRMA_DISPONIBLE";
    public const string SignedFile = "SIGNED_FILE";
    public const string FirmaTimeout = "FIRMA_TIMEOUT";
    public const string WordWritten = "WORD_WRITTEN";
    public const string WordContent = "WORD_CONTENT";
    public const string PdfCopyWritten = "PDF_COPY_WRITTEN";
    public const string FolderOpened = "FOLDER_OPENED";
    public const string PdfContent = "PDF_CONTENT";
    public const string WorkspaceWatching = "WORKSPACE_WATCHING";
    public const string WorkspaceChanged = "WORKSPACE_CHANGED";
    public const string Error = "ERROR";
}
