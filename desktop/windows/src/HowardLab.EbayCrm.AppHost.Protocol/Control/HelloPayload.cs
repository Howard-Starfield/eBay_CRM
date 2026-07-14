using System.Diagnostics;

namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

[DebuggerDisplay("ProcessId = {ProcessId}, ProcessCreationTimeUtcTicks = {ProcessCreationTimeUtcTicks}, CapabilityNonce = <redacted>, BuildIdentity = {BuildIdentity}, LoopbackEndpoint = {LoopbackEndpoint}")]
public sealed record HelloPayload(
    int ProcessId,
    long ProcessCreationTimeUtcTicks,
    string CapabilityNonce,
    string BuildIdentity,
    string? LoopbackEndpoint)
{
    public override string ToString() =>
        $"HelloPayload {{ ProcessId = {ProcessId}, ProcessCreationTimeUtcTicks = {ProcessCreationTimeUtcTicks}, CapabilityNonce = <redacted>, BuildIdentity = {BuildIdentity}, LoopbackEndpoint = {LoopbackEndpoint} }}";
}
