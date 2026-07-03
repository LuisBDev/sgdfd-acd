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

/// <summary>REQUEST_SIGNED_FILE — solicita el PDF firmado para enviarlo de vuelta.</summary>
public sealed record RequestSignedFileMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("filename")]
    string Filename);