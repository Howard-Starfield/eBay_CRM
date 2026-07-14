using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Time;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Lifecycle;

public sealed class RestartBudgetTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoRetriesAreAllowedWithinTheWindowAndTheThirdIsExhausted()
    {
        var budget = CreateBudget();

        Assert.Equal(RestartBudgetResult.Allowed, budget.TryConsume(RuntimeRole.Worker, Epoch));
        Assert.Equal(RestartBudgetResult.Allowed, budget.TryConsume(RuntimeRole.Worker, Epoch.AddSeconds(30)));
        Assert.Equal(RestartBudgetResult.Exhausted, budget.TryConsume(RuntimeRole.Worker, Epoch.AddSeconds(59)));
    }

    [Fact]
    public void AttemptsOutsideTheWindowDoNotExhaustTheBudget()
    {
        var budget = CreateBudget();

        Assert.Equal(RestartBudgetResult.Allowed, budget.TryConsume(RuntimeRole.Server, Epoch));
        Assert.Equal(RestartBudgetResult.Allowed, budget.TryConsume(RuntimeRole.Server, Epoch.AddSeconds(61)));
        Assert.Equal(RestartBudgetResult.Allowed, budget.TryConsume(RuntimeRole.Server, Epoch.AddSeconds(62)));
    }

    [Fact]
    public void InjectedStablePeriodResetsAttemptsWithoutSleeping()
    {
        var clock = new TestClock(Epoch);
        var budget = CreateBudget();
        Assert.Equal(RestartBudgetResult.Allowed, budget.TryConsume(RuntimeRole.Database, clock.UtcNow));
        budget.RecordStable(RuntimeRole.Database, clock.UtcNow);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(RestartBudgetResult.ResetAndAllowed, budget.TryConsume(RuntimeRole.Database, clock.UtcNow));
        Assert.Equal(RestartBudgetResult.Allowed, budget.TryConsume(RuntimeRole.Database, clock.UtcNow));
        Assert.Equal(RestartBudgetResult.Exhausted, budget.TryConsume(RuntimeRole.Database, clock.UtcNow));
    }

    [Fact]
    public void BudgetsAreIndependentPerRole()
    {
        var budget = CreateBudget();
        budget.TryConsume(RuntimeRole.Worker, Epoch);
        budget.TryConsume(RuntimeRole.Worker, Epoch);

        Assert.Equal(RestartBudgetResult.Exhausted, budget.TryConsume(RuntimeRole.Worker, Epoch));
        Assert.Equal(RestartBudgetResult.Allowed, budget.TryConsume(RuntimeRole.Server, Epoch));
    }

    [Fact]
    public void SystemClockReturnsAUtcValueFromTheCurrentInterval()
    {
        var before = DateTimeOffset.UtcNow;
        var actual = new SystemClock().UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(actual, before, after);
        Assert.Equal(TimeSpan.Zero, actual.Offset);
    }

    private static RestartBudget CreateBudget()
    {
        return new RestartBudget(2, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }
}
