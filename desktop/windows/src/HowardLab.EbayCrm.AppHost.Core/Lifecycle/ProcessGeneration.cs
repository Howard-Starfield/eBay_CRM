using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Lifecycle;

public readonly record struct ProcessGeneration(
    RuntimeRole Role,
    long Value,
    Guid OperationId);
