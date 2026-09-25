namespace ACD.DocumentEdit;

public sealed record DocumentEditRequest(
    Guid RequestId,
    string SafeFilename,
    long ExpectedSize,
    string ExpectedSha256);
