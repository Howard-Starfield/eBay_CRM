using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Processes;

public interface IProcessLauncher
{
    ValueTask<ISupervisedProcess> LaunchAsync(
        LaunchSpecification specification,
        IProcessGroup processGroup,
        CancellationToken cancellationToken);
}

public sealed class ProcessLaunchException : Exception
{
    public ProcessLaunchException(RuntimeRole role, int win32ErrorCode)
        : base($"Could not launch {role}; Win32 error {win32ErrorCode}.")
    {
        Role = role;
        Win32ErrorCode = win32ErrorCode;
    }

    public RuntimeRole Role { get; }

    public int Win32ErrorCode { get; }
}
