using ACD.Configuration;
using ACD.WebSocket.Messages;

namespace ACD.PdfOpen;

public static class PdfOpenRequestValidator
{
    public static bool TryValidate(
        OpenPdfMessage message,
        PdfOpenOptions options,
        out PdfOpenRequest? request,
        out string errorCode,
        out string errorMessage)
    {
        request = null;

        if (!Guid.TryParse(message.RequestId, out var requestId))
            return Fail(ErrorCatalog.InvalidRequestId, "requestId must be a valid UUID", out errorCode, out errorMessage);

        if (message.Size <= 0 || message.Size > options.MaxFileSizeBytes)
            return Fail(ErrorCatalog.InvalidFileSize, $"PDF size must be between 1 and {options.MaxFileSizeBytes} bytes", out errorCode, out errorMessage);

        var filename = message.Filename.Trim();
        if (filename.Length is 0 or > 180
            || !string.Equals(filename, Path.GetFileName(filename), StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(filename), ".pdf", StringComparison.OrdinalIgnoreCase)
            || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || filename.Contains("..", StringComparison.Ordinal))
            return Fail(ErrorCatalog.InvalidFilename, "filename must be a safe PDF file name", out errorCode, out errorMessage);

        var hash = message.Sha256.Trim();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            return Fail(ErrorCatalog.HashMismatch, "sha256 must contain 64 hexadecimal characters", out errorCode, out errorMessage);

        request = new PdfOpenRequest(requestId, filename, message.Size, hash.ToUpperInvariant());
        errorCode = string.Empty;
        errorMessage = string.Empty;
        return true;
    }

    private static bool Fail(string code, string message, out string errorCode, out string errorMessage)
    {
        errorCode = code;
        errorMessage = message;
        return false;
    }
}
