namespace ACD.Firma.Signing;

// Códigos de tipo de firma del contrato SGD (primer token del comando FirmaONPE).
// Pueden venir pelados ("2") o como "codigo$selloTiempo$cargo".
public static class FirmaTipo
{
    public const string NumerarFirmar = "1";
    public const string VistoBueno = "2";
    public const string Avanzada = "3";
    public const string Recepcion = "4";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>
    {
        NumerarFirmar, VistoBueno, Avanzada, Recepcion
    };

    // Todos los sufijos que FirmaONPE puede agregar, incluido el fallback [F].
    public static readonly IReadOnlyList<string> SignedSuffixes =
        new[] { "[F]", "[NF]", "[VF]", "[AF]", "[RF]" };

    public static bool IsSupported(string? tipo)
    {
        return tipo is not null && Supported.Contains(BaseCode(tipo));
    }


    public static string SignedSuffix(string? tipo, bool numera)
    {
        return BaseCode(tipo ?? "") switch
        {
            NumerarFirmar => numera ? "[NF]" : "[F]",
            VistoBueno => "[VF]",
            Avanzada => "[AF]",
            Recepcion => "[RF]",
            _ => "[F]"
        };
    }

    // Nombre que FirmaONPE le dará al firmado del PDF original: base + sufijo + .pdf.
    // Fuente única de la regla, compartida por el watcher (nombre esperado) y el
    // flujo de archivado (exclusión del firmado en vuelo).
    public static string SignedFileName(string originalFilename, string? tipo, bool numera)
    {
        return Path.GetFileNameWithoutExtension(originalFilename) + SignedSuffix(tipo, numera) + ".pdf";
    }

    private static string BaseCode(string tipo)
    {
        var i = tipo.IndexOf('$');
        return i >= 0 ? tipo[..i] : tipo;
    }
}