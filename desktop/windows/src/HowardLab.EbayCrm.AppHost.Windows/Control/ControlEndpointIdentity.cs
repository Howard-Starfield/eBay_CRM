using System.Diagnostics;
using System.Security.Cryptography;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Windows.Control;

[DebuggerDisplay("Role = {Role}, Generation = {Generation}, StartupOperationId = {StartupOperationId}, ExpectedBuildIdentity = {ExpectedBuildIdentity}, PipeName = <redacted>, CapabilityNonce = <redacted>")]
public sealed record ControlEndpointIdentity(
    string PipeName,
    string CapabilityNonce,
    RuntimeRole Role,
    long Generation,
    Guid StartupOperationId,
    string ExpectedBuildIdentity)
{
    internal static ControlEndpointIdentity Create(
        RuntimeRole role,
        long generation,
        Guid startupOperationId,
        string expectedBuildIdentity)
    {
        var pipeToken = CreateToken();
        return new ControlEndpointIdentity(
            $"HowardLab.EbayCrm.AppHost.{pipeToken}",
            CreateToken(),
            role,
            generation,
            startupOperationId,
            expectedBuildIdentity);
    }

    public override string ToString() =>
        $"ControlEndpointIdentity {{ PipeName = <redacted>, CapabilityNonce = <redacted>, Role = {Role}, Generation = {Generation}, StartupOperationId = {StartupOperationId}, ExpectedBuildIdentity = {ExpectedBuildIdentity} }}";

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public sealed record ControlChildEnvironment(
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, SecretValue> SecretEnvironment);
