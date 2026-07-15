using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Windows.Native;

namespace HowardLab.EbayCrm.AppHost.Windows.Postgres;

internal enum PostmasterProcessState
{
    Missing,
    PresentOrAmbiguous,
}

internal interface IPostmasterProcessProbe
{
    PostmasterProcessState Probe(int processId);
}

internal sealed class WindowsPostmasterProcessProbe : IPostmasterProcessProbe
{
    internal static WindowsPostmasterProcessProbe Instance { get; } = new();

    public PostmasterProcessState Probe(int processId)
    {
        using var handle = NativeMethods.OpenProcess(
            NativeMethods.Synchronize | NativeMethods.ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));
        if (!handle.IsInvalid)
        {
            return PostmasterProcessState.PresentOrAmbiguous;
        }

        const int errorInvalidParameter = 87;
        return Marshal.GetLastPInvokeError() == errorInvalidParameter
            ? PostmasterProcessState.Missing
            : PostmasterProcessState.PresentOrAmbiguous;
    }
}

internal static class PostmasterPidFileRepair
{
    internal static bool TryRepair(
        string path,
        string expectedDataDirectory,
        int expectedPort,
        IPostmasterProcessProbe processProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processProbe);
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        PostmasterPidFile parsed;
        try
        {
            parsed = PostmasterPidFile.Read(path, expectedDataDirectory);
        }
        catch (PostmasterPidFileException)
        {
            return false;
        }

        if (parsed.Port != expectedPort ||
            processProbe.Probe(parsed.ProcessId) != PostmasterProcessState.Missing)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        PostmasterPidFile confirmed;
        try
        {
            confirmed = PostmasterPidFile.Read(path, expectedDataDirectory);
        }
        catch (PostmasterPidFileException)
        {
            return false;
        }

        if (confirmed != parsed ||
            processProbe.Probe(confirmed.ProcessId) != PostmasterProcessState.Missing)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
