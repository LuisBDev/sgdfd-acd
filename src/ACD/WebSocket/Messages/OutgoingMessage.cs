using System.Text.Json.Serialization;

namespace ACD.WebSocket.Messages;

/// <summary>CONNECTED — enviado inmediatamente después del WebSocket upgrade, antes que cualquier otro mensaje.</summary>
public sealed record ConnectedMessage(
    [property: JsonPropertyName("version")]
    string Version,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("watchDir")]
    string WatchDir)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.Connected;
}

public sealed record AuthOkMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.AuthOk;
}

public sealed record PdfReceivedMessage(
    [property: JsonPropertyName("filename")]
    string Filename)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.PdfReceived;
}

public sealed record FirmaDisponibleMessage(
    [property: JsonPropertyName("filename")]
    string Filename)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.FirmaDisponible;
}

/// <summary>SIGNED_FILE — anuncia la transferencia binaria del archivo firmado. El siguiente frame es binario.</summary>
public sealed record SignedFileMessage(
    [property: JsonPropertyName("filename")]
    string Filename,
    [property: JsonPropertyName("expectedSize")]
    long ExpectedSize)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.SignedFile;
}

public sealed record FirmaTimeoutMessage(
    [property: JsonPropertyName("filename")]
    string Filename,
    [property: JsonPropertyName("timeoutSeconds")]
    int TimeoutSeconds)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.FirmaTimeout;
}

public sealed record ErrorMessage(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")]
    string Message,
    [property: JsonPropertyName("category")]
    string Category)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.Error;
}