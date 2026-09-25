using System.Text.Json.Serialization;

namespace ACD.WebSocket.Messages;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BaseMessage))]
[JsonSerializable(typeof(AuthMessage))]
[JsonSerializable(typeof(PdfDownloadMessage))]
[JsonSerializable(typeof(OpenPdfMessage))]
[JsonSerializable(typeof(RequestSignedFileMessage))]
[JsonSerializable(typeof(EditDocumentMessage))]
[JsonSerializable(typeof(RequestEditedPdfMessage))]
[JsonSerializable(typeof(CancelEditMessage))]
[JsonSerializable(typeof(AuthOkMessage))]
[JsonSerializable(typeof(ConnectedMessage))]
[JsonSerializable(typeof(PdfReceivedMessage))]
[JsonSerializable(typeof(PdfOpenedMessage))]
[JsonSerializable(typeof(FirmaDisponibleMessage))]
[JsonSerializable(typeof(SignedFileMessage))]
[JsonSerializable(typeof(FirmaTimeoutMessage))]
[JsonSerializable(typeof(DocumentOpenedMessage))]
[JsonSerializable(typeof(EditedPdfReadyMessage))]
[JsonSerializable(typeof(EditedPdfMessage))]
[JsonSerializable(typeof(EditTimeoutMessage))]
[JsonSerializable(typeof(ErrorMessage))]
public partial class AcdJsonContext : JsonSerializerContext
{
}
