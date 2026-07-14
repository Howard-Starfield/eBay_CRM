namespace HowardLab.EbayCrm.AppHost.Core.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
