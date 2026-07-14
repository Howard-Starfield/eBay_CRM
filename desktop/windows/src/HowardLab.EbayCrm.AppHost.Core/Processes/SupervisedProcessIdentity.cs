using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Processes;

public sealed record SupervisedProcessIdentity(
    RuntimeRole Role,
    ProcessGeneration Generation,
    int ProcessId,
    DateTimeOffset CreationTimeUtc,
    string VerifiedImagePath);
