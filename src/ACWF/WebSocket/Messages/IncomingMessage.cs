using System.Text.Json.Serialization;

namespace ACWF.WebSocket.Messages;

/// <summary>Discriminator record used to read the "type" field before full deserialization.</summary>
public sealed record BaseMessage(
    [property: JsonPropertyName("type")] string Type);

/// <summary>AUTH — Bearer token exchange. Must be the first message after CONNECTED.</summary>
public sealed record AuthMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("token")] string Token);

/// <summary>PDF_DOWNLOAD — announces incoming PDF binary frame. Next frame is binary.</summary>
public sealed record PdfDownloadMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("size")] long Size);

/// <summary>REQUEST_SIGNED_FILE — requests the signed PDF for sending back.</summary>
public sealed record RequestSignedFileMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("filename")] string Filename);
