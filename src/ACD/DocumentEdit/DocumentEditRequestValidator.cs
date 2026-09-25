using System.Security.Cryptography;
using ACD.Configuration;
using ACD.WebSocket.Messages;

namespace ACD.DocumentEdit;

public static class DocumentEditRequestValidator
{
    public static bool TryValidate(
        string? requestId,
        string? filename,
        long size,
        string? sha256,
        DocumentEditOptions options,
        out DocumentEditRequest? request,
        out string errorCode,
        out string errorMessage)
    {
        request = null;

        if (!Guid.TryParse(requestId, out var parsedRequestId))
            return Fail(ErrorCatalog.InvalidRequestId, "requestId must be a valid UUID", out errorCode, out errorMessage);

        if (size <= 0 || size > options.MaxFileBytes)
            return Fail(ErrorCatalog.InvalidFileSize, $"Document size must be between 1 and {options.MaxFileBytes} bytes", out errorCode, out errorMessage);

        var safeFilename = filename?.Trim() ?? string.Empty;
        if (safeFilename.Length is 0 or > 180
            || !string.Equals(safeFilename, Path.GetFileName(safeFilename), StringComparison.Ordinal)
            || !IsAllowedExtension(Path.GetExtension(safeFilename), options)
            || Path.GetFileNameWithoutExtension(safeFilename).Trim().Length == 0
            || safeFilename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || safeFilename.Contains("..", StringComparison.Ordinal))
            return Fail(ErrorCatalog.InvalidFilename, "filename must be a safe document file name with an allowed extension", out errorCode, out errorMessage);

        var hash = sha256?.Trim() ?? string.Empty;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            return Fail(ErrorCatalog.HashMismatch, "sha256 must contain 64 hexadecimal characters", out errorCode, out errorMessage);

        request = new DocumentEditRequest(parsedRequestId, safeFilename, size, hash.ToUpperInvariant());
        errorCode = string.Empty;
        errorMessage = string.Empty;
        return true;
    }

    public static bool TryVerifyContent(
        DocumentEditRequest request,
        byte[] data,
        out string errorCode,
        out string errorMessage)
    {
        if (data.LongLength != request.ExpectedSize)
            return Fail(ErrorCatalog.InvalidFileSize, "Binary payload size does not match EDIT_DOCUMENT metadata", out errorCode, out errorMessage);

        var actualHash = SHA256.HashData(data);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(request.ExpectedSha256)))
            return Fail(ErrorCatalog.HashMismatch, "Document checksum does not match", out errorCode, out errorMessage);

        errorCode = string.Empty;
        errorMessage = string.Empty;
        return true;
    }

    private static bool IsAllowedExtension(string extension, DocumentEditOptions options) =>
        extension.Length > 0
        && options.AllowedExtensions.Any(allowed => string.Equals(allowed, extension, StringComparison.OrdinalIgnoreCase));

    private static bool Fail(string code, string message, out string errorCode, out string errorMessage)
    {
        errorCode = code;
        errorMessage = message;
        return false;
    }
}
