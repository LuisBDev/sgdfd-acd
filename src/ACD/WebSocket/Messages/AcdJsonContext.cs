using System.Text.Json.Serialization;

namespace ACD.WebSocket.Messages;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BaseMessage))]
[JsonSerializable(typeof(AuthMessage))]
[JsonSerializable(typeof(PdfDownloadMessage))]
[JsonSerializable(typeof(OpenPdfMessage))]
[JsonSerializable(typeof(RequestSignedFileMessage))]
[JsonSerializable(typeof(AuthOkMessage))]
[JsonSerializable(typeof(ConnectedMessage))]
[JsonSerializable(typeof(PdfReceivedMessage))]
[JsonSerializable(typeof(PdfOpenedMessage))]
[JsonSerializable(typeof(FirmaDisponibleMessage))]
[JsonSerializable(typeof(SignedFileMessage))]
[JsonSerializable(typeof(FirmaTimeoutMessage))]
[JsonSerializable(typeof(WorkspaceStatusMessage))]
[JsonSerializable(typeof(WriteWordMessage))]
[JsonSerializable(typeof(ReadWordMessage))]
[JsonSerializable(typeof(WritePdfCopyMessage))]
[JsonSerializable(typeof(OpenFolderMessage))]
[JsonSerializable(typeof(ConvertToPdfMessage))]
[JsonSerializable(typeof(WorkspaceStatusResultMessage))]
[JsonSerializable(typeof(WordWrittenMessage))]
[JsonSerializable(typeof(WordContentMessage))]
[JsonSerializable(typeof(PdfCopyWrittenMessage))]
[JsonSerializable(typeof(FolderOpenedMessage))]
[JsonSerializable(typeof(PdfContentMessage))]
[JsonSerializable(typeof(ErrorMessage))]
public partial class AcdJsonContext : JsonSerializerContext
{
}
