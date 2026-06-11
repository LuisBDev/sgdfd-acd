namespace ACWF.Firma;

public interface IFileDepositService
{
    Task<string> DepositAsync(string filename, Stream content, CancellationToken ct);
}
