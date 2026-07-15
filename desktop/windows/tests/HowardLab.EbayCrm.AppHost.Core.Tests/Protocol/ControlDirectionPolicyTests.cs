using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Protocol;

public sealed class ControlDirectionPolicyTests
{
    [Fact]
    public void ImplementsTheCompleteRoleTypeDirectionMatrix()
    {
        var allowed = new HashSet<(RuntimeRole Role, ControlMessageType Type, ControlDirection Direction)>
        {
            (RuntimeRole.Server, ControlMessageType.IdentityChallenge, ControlDirection.AppHostToChild),
            (RuntimeRole.Server, ControlMessageType.Shutdown, ControlDirection.AppHostToChild),
            (RuntimeRole.Server, ControlMessageType.Hello, ControlDirection.ChildToAppHost),
            (RuntimeRole.Server, ControlMessageType.Health, ControlDirection.ChildToAppHost),
            (RuntimeRole.Server, ControlMessageType.ShutdownAccepted, ControlDirection.ChildToAppHost),
            (RuntimeRole.Server, ControlMessageType.Stopped, ControlDirection.ChildToAppHost),
            (RuntimeRole.Worker, ControlMessageType.IdentityChallenge, ControlDirection.AppHostToChild),
            (RuntimeRole.Worker, ControlMessageType.Drain, ControlDirection.AppHostToChild),
            (RuntimeRole.Worker, ControlMessageType.Shutdown, ControlDirection.AppHostToChild),
            (RuntimeRole.Worker, ControlMessageType.Hello, ControlDirection.ChildToAppHost),
            (RuntimeRole.Worker, ControlMessageType.Health, ControlDirection.ChildToAppHost),
            (RuntimeRole.Worker, ControlMessageType.DrainAccepted, ControlDirection.ChildToAppHost),
            (RuntimeRole.Worker, ControlMessageType.NoNewWorkAcquisition, ControlDirection.ChildToAppHost),
            (RuntimeRole.Worker, ControlMessageType.ActiveWorkRemaining, ControlDirection.ChildToAppHost),
            (RuntimeRole.Worker, ControlMessageType.Drained, ControlDirection.ChildToAppHost),
            (RuntimeRole.Worker, ControlMessageType.ShutdownAccepted, ControlDirection.ChildToAppHost),
            (RuntimeRole.Worker, ControlMessageType.Stopped, ControlDirection.ChildToAppHost),
        };

        foreach (var role in Enum.GetValues<RuntimeRole>())
        {
            foreach (var type in Enum.GetValues<ControlMessageType>())
            {
                foreach (var direction in Enum.GetValues<ControlDirection>())
                {
                    Assert.Equal(
                        allowed.Contains((role, type, direction)),
                        ControlDirectionPolicy.IsAllowed(role, type, direction));
                }
            }
        }
    }

    [Theory]
    [InlineData(RuntimeRole.Server, ControlMessageType.Drain, ControlDirection.AppHostToChild)]
    [InlineData(RuntimeRole.Server, ControlMessageType.DrainAccepted, ControlDirection.ChildToAppHost)]
    [InlineData(RuntimeRole.Server, ControlMessageType.NoNewWorkAcquisition, ControlDirection.ChildToAppHost)]
    [InlineData(RuntimeRole.Server, ControlMessageType.ActiveWorkRemaining, ControlDirection.ChildToAppHost)]
    [InlineData(RuntimeRole.Server, ControlMessageType.Drained, ControlDirection.ChildToAppHost)]
    [InlineData(RuntimeRole.Database, ControlMessageType.IdentityChallenge, ControlDirection.AppHostToChild)]
    [InlineData(RuntimeRole.Database, ControlMessageType.Hello, ControlDirection.ChildToAppHost)]
    [InlineData(RuntimeRole.Database, ControlMessageType.Shutdown, ControlDirection.AppHostToChild)]
    public void ExplicitlyRejectsRoleBoundaryViolations(
        RuntimeRole role,
        ControlMessageType type,
        ControlDirection direction)
    {
        Assert.False(ControlDirectionPolicy.IsAllowed(role, type, direction));
    }
}
