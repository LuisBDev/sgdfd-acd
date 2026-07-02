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

    public static bool IsSupported(string? tipo) => tipo is not null && Supported.Contains(BaseCode(tipo));

    // Todos los sufijos que FirmaONPE puede agregar, incluido el fallback [F].
    public static readonly IReadOnlyList<string> SignedSuffixes =
        new[] { "[F]", "[NF]", "[VF]", "[AF]", "[RF]" };

    // Sufijo que FirmaONPE agrega al PDF firmado según el tipo (ver ValidarDatos).
    public static string SignedSuffix(string? tipo) => BaseCode(tipo ?? "") switch
    {
        NumerarFirmar => "[NF]",
        VistoBueno => "[VF]",
        Avanzada => "[AF]",
        Recepcion => "[RF]",
        _ => "[F]"
    };

    // Nombre que FirmaONPE le dará al firmado del PDF original: base + sufijo + .pdf.
    // Fuente única de la regla, compartida por el watcher (nombre esperado) y el
    // flujo de archivado (exclusión del firmado en vuelo).
    public static string SignedFileName(string originalFilename, string? tipo) =>
        Path.GetFileNameWithoutExtension(originalFilename) + SignedSuffix(tipo) + ".pdf";

    private static string BaseCode(string tipo)
    {
        var i = tipo.IndexOf('$');
        return i >= 0 ? tipo[..i] : tipo;
    }
}
