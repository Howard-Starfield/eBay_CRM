using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Lifecycle;

public sealed class RestartBudget
{
    private readonly int _maxRetries;
    private readonly TimeSpan _window;
    private readonly TimeSpan _stableResetPeriod;
    private readonly Dictionary<RuntimeRole, RoleBudget> _roles = [];

    public RestartBudget(int maxRetries, TimeSpan window, TimeSpan stableResetPeriod)
    {
        _maxRetries = maxRetries;
        _window = window;
        _stableResetPeriod = stableResetPeriod;
    }

    public RestartBudgetResult TryConsume(RuntimeRole role, DateTimeOffset now)
    {
        var budget = GetBudget(role);
        var reset = budget.StableSince is { } stableSince && now - stableSince >= _stableResetPeriod;
        if (reset)
        {
            budget.Attempts.Clear();
        }

        budget.StableSince = null;
        var cutoff = now - _window;
        while (budget.Attempts.Count > 0 && budget.Attempts.Peek() < cutoff)
        {
            budget.Attempts.Dequeue();
        }

        if (budget.Attempts.Count >= _maxRetries)
        {
            return RestartBudgetResult.Exhausted;
        }

        budget.Attempts.Enqueue(now);
        return reset ? RestartBudgetResult.ResetAndAllowed : RestartBudgetResult.Allowed;
    }

    public void RecordStable(RuntimeRole role, DateTimeOffset now)
    {
        GetBudget(role).StableSince = now;
    }

    private RoleBudget GetBudget(RuntimeRole role)
    {
        if (!_roles.TryGetValue(role, out var budget))
        {
            budget = new RoleBudget();
            _roles.Add(role, budget);
        }

        return budget;
    }

    private sealed class RoleBudget
    {
        public Queue<DateTimeOffset> Attempts { get; } = new();

        public DateTimeOffset? StableSince { get; set; }
    }
}

public enum RestartBudgetResult
{
    Allowed,
    Exhausted,
    ResetAndAllowed,
}
