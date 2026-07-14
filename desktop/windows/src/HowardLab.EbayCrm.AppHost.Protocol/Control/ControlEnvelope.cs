using System.Diagnostics;
using System.Text.Json;

namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

[DebuggerDisplay("Version = {Version}, OperationId = {OperationId}, Role = {Role}, Generation = {Generation}, Type = {Type}, Payload = <redacted>")]
public sealed record ControlEnvelope(
    int Version,
    Guid OperationId,
    RuntimeRole Role,
    long Generation,
    ControlMessageType Type,
    JsonElement Payload)
{
    public override string ToString() =>
        $"ControlEnvelope {{ Version = {Version}, OperationId = {OperationId}, Role = {Role}, Generation = {Generation}, Type = {Type}, Payload = <redacted> }}";
}
