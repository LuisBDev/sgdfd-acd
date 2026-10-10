using ACD.WebSocket.Messages;

namespace ACD.DocumentWorkspace;

internal sealed record WorkspaceRejection(string Code, string Message, int CloseCode);

internal static class WorkspaceRequestValidation
{
    internal const int PolicyViolation = 1008;

    internal static bool TryResolveRemito(
        WorkspacePaths paths,
        string? requestId,
        string? anio,
        string? numeroEmision,
        out string directory,
        out WorkspaceRejection? rejection)
    {
        directory = string.Empty;
        if (!TryValidateRequestId(requestId, out rejection))
            return false;

        if (!paths.TryGetRemitoDirectory(anio!, numeroEmision!, out directory))
        {
            rejection = new WorkspaceRejection(ErrorCatalog.WorkspaceInvalidKey, "anio must have 4 digits and numeroEmision 1 to 10 digits", PolicyViolation);
            return false;
        }

        rejection = null;
        return true;
    }

    internal static bool TryValidateRequestId(string? requestId, out WorkspaceRejection? rejection)
    {
        rejection = Guid.TryParse(requestId, out _)
            ? null
            : new WorkspaceRejection(ErrorCatalog.InvalidRequestId, "requestId must be a valid UUID", PolicyViolation);
        return rejection is null;
    }
}
