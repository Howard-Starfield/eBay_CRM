namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

public sealed record HealthPayload(
    int ProtocolVersion,
    string BuildIdentity,
    long Generation,
    string GenerationNonce,
    string Status,
    int ActiveWorkRemaining);
