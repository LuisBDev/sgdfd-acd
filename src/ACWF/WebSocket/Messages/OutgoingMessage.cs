using System.Text.Json.Serialization;

namespace ACWF.WebSocket.Messages;

/// <summary>CONNECTED — sent immediately after WebSocket upgrade, before any other message.</summary>
public sealed record ConnectedMessage(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("watchDir")] string WatchDir)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageType.Connected;
}

/// <summary>PDF_RECEIVED — confirms PDF written to disk.</summary>
public sealed record PdfReceivedMessage(
    [property: JsonPropertyName("filename")] string Filename)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageType.PdfReceived;
}

/// <summary>FIRMA_DISPONIBLE — notifies that the signed file is available.</summary>
public sealed record FirmaDisponibleMessage(
    [property: JsonPropertyName("filename")] string Filename)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageType.FirmaDisponible;
}

/// <summary>SIGNED_FILE — announces binary signed file transfer. Next frame is binary.</summary>
public sealed record SignedFileMessage(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("size")] long Size)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageType.SignedFile;
}

/// <summary>FIRMA_TIMEOUT — signals timeout waiting for signed file.</summary>
public sealed record FirmaTimeoutMessage(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("timeoutSeconds")] int TimeoutSeconds)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageType.FirmaTimeout;
}

/// <summary>ERROR — generic error response.</summary>
public sealed record ErrorMessage(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageType.Error;
}
