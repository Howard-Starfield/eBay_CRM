namespace HowardLab.EbayCrm.AppHost.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
