using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Processes;

public sealed record LaunchSpecification(
    RuntimeRole Role,
    ProcessGeneration Generation,
    string ApplicationPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, SecretValue> SecretEnvironment,
    TimeSpan OutputDrainTimeout,
    ReadOnlyMemory<byte> StandardInput = default);
