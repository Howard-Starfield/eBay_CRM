using System.Diagnostics;
using System.Globalization;

namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

[DebuggerDisplay("ProcessId = {ProcessId}, ProcessCreationTimeUtcTicks = {ProcessCreationTimeUtcTicks}, ChallengeId = <redacted>")]
public sealed record IdentityChallengePayload(
    int ProcessId,
    string ProcessCreationTimeUtcTicks,
    string ChallengeId)
{
    public override string ToString() =>
        $"IdentityChallengePayload {{ ProcessId = {ProcessId}, ProcessCreationTimeUtcTicks = {ProcessCreationTimeUtcTicks}, ChallengeId = <redacted> }}";
}

public static class ProcessCreationTimeTicks
{
    public static bool TryParseCanonical(string? value, out long ticks)
    {
        ticks = 0;
        return value is not null &&
            value.Length is > 0 and <= 19 &&
            value[0] is >= '1' and <= '9' &&
            value.All(character => character is >= '0' and <= '9') &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ticks) &&
            ticks > 0 &&
            string.Equals(ticks.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal);
    }

    public static string Format(long ticks)
    {
        if (ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks));
        }

        return ticks.ToString(CultureInfo.InvariantCulture);
    }
}
