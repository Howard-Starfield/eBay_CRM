using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Composition;

internal interface IRoleLaunchPlanProvider
{
    RoleLaunchPlan Create(RoleLaunchRequest request);
}

internal sealed record RoleLaunchRequest(RuntimeRole Role, ProcessGeneration Generation);
