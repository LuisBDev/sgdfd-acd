using System.Text.RegularExpressions;
using ACD.Configuration;

namespace ACD.DocumentWorkspace;

public sealed partial class WorkspacePaths(DocumentEditOptions options)
{
    public bool TryGetRemitoDirectory(string anio, string numeroEmision, out string directory)
    {
        if (anio is null || numeroEmision is null || !AnioPattern().IsMatch(anio) || !NumeroEmisionPattern().IsMatch(numeroEmision))
        {
            directory = string.Empty;
            return false;
        }

        directory = Path.Combine(options.GetRootDirectory(), anio, numeroEmision);
        return true;
    }

    [GeneratedRegex(@"^[0-9]{4}\z")]
    private static partial Regex AnioPattern();

    [GeneratedRegex(@"^[0-9]{1,10}\z")]
    private static partial Regex NumeroEmisionPattern();
}
