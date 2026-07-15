namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public static class NodePayloadPath
{
    public const int MaxChars = 512;

    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsCanonicalRelative(string? path)
    {
        if (string.IsNullOrEmpty(path) ||
            path.Length > MaxChars ||
            path[0] == '/' ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal) ||
            path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment[^1] is '.' or ' ' ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                IsReservedDeviceName(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var extension = segment.IndexOf('.');
        var deviceName = extension < 0 ? segment : segment[..extension];
        return ReservedDeviceNames.Contains(deviceName) ||
            deviceName.Length == 4 &&
            (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            deviceName[3] is '¹' or '²' or '³';
    }
}
