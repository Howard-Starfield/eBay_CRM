using HowardLab.EbayCrm.AppHost.Core.Time;

namespace HowardLab.EbayCrm.AppHost.Core.Tests;

internal sealed class TestClock(DateTimeOffset initial) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = initial;

    public void Advance(TimeSpan duration)
    {
        UtcNow += duration;
    }
}
