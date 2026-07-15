namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public sealed class NodePayloadManifestException : Exception
{
    public const string TrustFailureReason = "role-payload-trust-failed";

    internal NodePayloadManifestException()
        : base(TrustFailureReason)
    {
    }

    public string ReasonCode => TrustFailureReason;
}
