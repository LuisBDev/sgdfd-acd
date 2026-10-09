namespace ACD.DocumentWorkspace;

public interface IConversionService
{
    Task<byte[]> ConvertDocxToPdfAsync(byte[] docx, CancellationToken ct);
}
