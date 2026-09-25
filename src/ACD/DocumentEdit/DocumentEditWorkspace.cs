namespace ACD.DocumentEdit;

public sealed record DocumentEditWorkspace(
    Guid RequestId,
    string DirectoryPath,
    string SourcePath);
