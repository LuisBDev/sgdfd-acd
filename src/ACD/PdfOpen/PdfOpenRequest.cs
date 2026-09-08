namespace ACD.PdfOpen;

public sealed record PdfOpenRequest(
    Guid RequestId,
    string SafeFilename,
    long ExpectedSize,
    string ExpectedSha256);
