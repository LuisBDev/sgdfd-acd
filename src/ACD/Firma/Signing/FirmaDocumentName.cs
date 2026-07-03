using System.Globalization;
using System.Text;

namespace ACD.Firma.Signing;

/// <summary>
///     Construye el nombre canónico del PDF: "{tipo} {numero}_{timestamp}.pdf".
///     El timestamp evita colisiones entre lanzamientos.
/// </summary>
public static class FirmaDocumentName
{
    public const string TimestampFormat = "yyyyMMdd_HHmmss";
    private const string Fallback = "documento";

    /// <summary>
    ///     Devuelve "{tipo} {numero}_{yyyyMMdd_HHmmss}.pdf" saneado para uso simultáneo
    ///     como ruta de archivo Windows y como argumento del comando FirmaONPE.
    /// </summary>
    public static string Build(string? tipoDocumento, string? numeroDocumento, DateTime timestamp)
    {
        var parts = new[] { Sanitize(tipoDocumento), Sanitize(numeroDocumento) }
            .Where(part => part.Length > 0);

        var baseName = string.Join(' ', parts);
        if (baseName.Length == 0) baseName = Fallback;

        var stamp = timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{baseName}_{stamp}.pdf";
    }

    /// <summary>
    ///     Arma la gramática de nombre que FirmaONPE parsea para estampar la numeración:
    ///     "TEMPR{timestamp}${numeracion}.pdf". El timestamp en la parte 0 (que FirmaONPE
    ///     ignora) mantiene la unicidad; las 7 partes de {numeracion} las provee el backend.
    /// </summary>
    public static string BuildFromNumeracion(string numeracion, DateTime timestamp)
    {
        var stamp = timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"TEMPR{stamp}${StripInvalidPathChars(numeracion)}.pdf";
    }

    // Quita solo caracteres inválidos de path Windows; preserva '$', '!', espacios y tildes.
    private static string StripInvalidPathChars(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (Array.IndexOf(invalid, ch) < 0)
                sb.Append(ch);
        return sb.ToString();
    }

    // Normaliza (NFD), reemplaza caracteres inválidos de path Windows por espacios, colapsa.
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? ' ' : ch);
        }

        return string.Join(' ', sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
