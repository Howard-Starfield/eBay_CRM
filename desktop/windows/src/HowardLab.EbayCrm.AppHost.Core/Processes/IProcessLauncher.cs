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

public enum NativeExitObservationKind
{
    Authoritative,
    ProofUnavailableContainment,
}

public sealed class ProcessCleanupException : Exception
{
    public ProcessCleanupException(
        RuntimeRole role,
        int? processTerminationErrorCode,
        int? jobTerminationErrorCode,
        int? waitErrorCode,
        bool timedOut,
        Task nativeExitObservation,
        NativeExitObservationKind nativeExitObservationKind =
            NativeExitObservationKind.Authoritative)
        : base(
            $"Could not complete {role} process cleanup; " +
            $"process error {Format(processTerminationErrorCode)}, " +
            $"job error {Format(jobTerminationErrorCode)}, " +
            $"wait error {Format(waitErrorCode)}, timed out {timedOut}.")
    {
        Role = role;
        ProcessTerminationErrorCode = processTerminationErrorCode;
        JobTerminationErrorCode = jobTerminationErrorCode;
        WaitErrorCode = waitErrorCode;
        TimedOut = timedOut;
        PayloadLifetimeBoundaryObservation = nativeExitObservation ??
            throw new ArgumentNullException(nameof(nativeExitObservation));
        NativeExitObservationKind = nativeExitObservationKind;
    }

    public RuntimeRole Role { get; }

    public int? ProcessTerminationErrorCode { get; }

    public int? JobTerminationErrorCode { get; }

    public int? WaitErrorCode { get; }

    public bool TimedOut { get; }

    public Task PayloadLifetimeBoundaryObservation { get; }

    public NativeExitObservationKind NativeExitObservationKind { get; }

    public Task? AuthoritativeNativeExitObservation =>
        NativeExitObservationKind == NativeExitObservationKind.Authoritative
            ? PayloadLifetimeBoundaryObservation
            : null;

    private static string Format(int? errorCode) => errorCode?.ToString() ?? "none";
}
