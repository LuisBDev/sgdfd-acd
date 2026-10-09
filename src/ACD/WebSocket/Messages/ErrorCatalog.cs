namespace ACD.WebSocket.Messages;

public static class ErrorCategory
{
    public const string UserActionable = "USER_ACTIONABLE";
    public const string Transient = "TRANSIENT";
    public const string System = "SYSTEM";
}

public static class ErrorCatalog
{
    public const string AuthRequired = "AUTH_REQUIRED";
    public const string SessionBusy = "SESSION_BUSY";
    public const string InvalidFilename = "INVALID_FILENAME";
    public const string InvalidRequestId = "INVALID_REQUEST_ID";
    public const string InvalidFileSize = "INVALID_FILE_SIZE";
    public const string PdfInvalid = "PDF_INVALID";
    public const string HashMismatch = "HASH_MISMATCH";
    public const string PdfOpenFailed = "PDF_OPEN_FAILED";
    public const string StorageLimitExceeded = "STORAGE_LIMIT_EXCEEDED";
    public const string MissingFirmaTipo = "MISSING_FIRMA_TIPO";
    public const string UnsupportedFirmaTipo = "UNSUPPORTED_FIRMA_TIPO";
    public const string WriteFailed = "WRITE_FAILED";
    public const string FirmaLaunchFailed = "FIRMA_LAUNCH_FAILED";
    public const string ProcessStartFailed = "PROCESS_START_FAILED";
    public const string FileLocked = "FILE_LOCKED";
    public const string ReadFailed = "READ_FAILED";
    public const string UnexpectedMessage = "UNEXPECTED_MESSAGE";
    public const string UnknownMessageType = "UNKNOWN_MESSAGE_TYPE";
    public const string InternalError = "INTERNAL_ERROR";
    public const string WorkspaceInvalidKey = "WORKSPACE_INVALID_KEY";
    public const string WorkspaceInvalidFilename = "WORKSPACE_INVALID_FILENAME";
    public const string WorkspaceFileExists = "WORKSPACE_FILE_EXISTS";
    public const string WorkspaceFileNotFound = "WORKSPACE_FILE_NOT_FOUND";
    public const string WorkspaceFolderNotFound = "WORKSPACE_FOLDER_NOT_FOUND";
    public const string WorkspaceIntegrity = "WORKSPACE_INTEGRITY";
    public const string WorkspaceIoFailed = "WORKSPACE_IO_FAILED";
    public const string WordNotInstalled = "WORD_NOT_INSTALLED";
    public const string ConversionTimeout = "CONVERSION_TIMEOUT";
    public const string ConversionFailed = "CONVERSION_FAILED";
    public const string WorkspaceWatchFailed = "WORKSPACE_WATCH_FAILED";

    private static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        [AuthRequired] = ErrorCategory.System,
        [SessionBusy] = ErrorCategory.Transient,
        [InvalidFilename] = ErrorCategory.System,
        [InvalidRequestId] = ErrorCategory.System,
        [InvalidFileSize] = ErrorCategory.System,
        [PdfInvalid] = ErrorCategory.System,
        [HashMismatch] = ErrorCategory.System,
        [PdfOpenFailed] = ErrorCategory.UserActionable,
        [StorageLimitExceeded] = ErrorCategory.Transient,
        [MissingFirmaTipo] = ErrorCategory.System,
        [UnsupportedFirmaTipo] = ErrorCategory.System,
        [WriteFailed] = ErrorCategory.Transient,
        [FirmaLaunchFailed] = ErrorCategory.UserActionable,
        [ProcessStartFailed] = ErrorCategory.UserActionable,
        [FileLocked] = ErrorCategory.UserActionable,
        [ReadFailed] = ErrorCategory.Transient,
        [UnexpectedMessage] = ErrorCategory.System,
        [UnknownMessageType] = ErrorCategory.System,
        [InternalError] = ErrorCategory.System,
        [WorkspaceInvalidKey] = ErrorCategory.UserActionable,
        [WorkspaceInvalidFilename] = ErrorCategory.UserActionable,
        [WorkspaceFileExists] = ErrorCategory.UserActionable,
        [WorkspaceFileNotFound] = ErrorCategory.UserActionable,
        [WorkspaceFolderNotFound] = ErrorCategory.UserActionable,
        [WorkspaceIntegrity] = ErrorCategory.Transient,
        [WorkspaceIoFailed] = ErrorCategory.Transient,
        [WordNotInstalled] = ErrorCategory.UserActionable,
        [ConversionTimeout] = ErrorCategory.Transient,
        [ConversionFailed] = ErrorCategory.System,
        [WorkspaceWatchFailed] = ErrorCategory.System,
    };

    public static string CategoryOf(string code) =>
        Categories.TryGetValue(code, out var category) ? category : ErrorCategory.System;
}
