using System.Text.Json.Serialization;

namespace ACD.WebSocket.Messages;

/// <summary>Record discriminador usado para leer el campo "type" antes de la deserialización completa.</summary>
public sealed record BaseMessage(
    [property: JsonPropertyName("type")] string Type);

/// <summary>AUTH — intercambio de Bearer token. Debe ser el primer mensaje después de CONNECTED.</summary>
public sealed record AuthMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("token")] string Token);

/// <summary>PDF_DOWNLOAD — anuncia la recepción del PDF. El siguiente frame contiene los datos binarios.</summary>
public sealed record PdfDownloadMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("tipoDocumento")]
    string TipoDocumento,
    [property: JsonPropertyName("numeroDocumento")]
    string NumeroDocumento,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("tipoFirma")]
    string? Tipo = null,
    [property: JsonPropertyName("numeracion")]
    string? Numeracion = null);

/// <summary>
///     OPEN_PDF — anuncia un PDF que debe abrirse con la aplicación predeterminada de Windows.
///     El siguiente frame contiene exactamente <see cref="Size"/> bytes.
/// </summary>
public sealed record OpenPdfMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record RequestSignedFileMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("filename")]
    string Filename);
