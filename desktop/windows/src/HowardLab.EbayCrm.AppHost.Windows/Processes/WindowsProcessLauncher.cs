using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Processes;

public sealed class WindowsProcessLauncher : IProcessLauncher
{
    public const int MaximumStandardInputBytes = 1024 * 1024;
    private static readonly Task ProofUnavailableContainment = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously).Task;
    private readonly IDiagnosticSink _diagnosticSink;
    private readonly int _maxOutputBytes;
    private readonly int _maxLineBytes;
    private readonly IProcessCleanupPolicy _cleanupPolicy;
    private readonly IWindowsProcessIdentityVerifier _identityVerifier;
    private readonly IWindowsNativeExitObservationFactory _nativeExitObservationFactory;

    public WindowsProcessLauncher(
        IDiagnosticSink diagnosticSink,
        int maxOutputBytes = 1024 * 1024,
        int maxLineBytes = 64 * 1024,
        TimeSpan? processCleanupTimeout = null)
        : this(
            diagnosticSink,
            maxOutputBytes,
            maxLineBytes,
            new BoundedProcessCleanup(
                NativeProcessCleanup.Instance,
                processCleanupTimeout ?? BoundedProcessCleanup.ProductionDefaultTimeout),
            WindowsProcessIdentityVerifier.Instance,
            WindowsNativeExitObservationFactory.Instance)
    {
    }

    internal WindowsProcessLauncher(
        IDiagnosticSink diagnosticSink,
        int maxOutputBytes,
        int maxLineBytes,
        IProcessCleanupPolicy cleanupPolicy,
        IWindowsProcessIdentityVerifier identityVerifier,
        IWindowsNativeExitObservationFactory? nativeExitObservationFactory = null)
    {
        ArgumentNullException.ThrowIfNull(diagnosticSink);
        ArgumentNullException.ThrowIfNull(cleanupPolicy);
        ArgumentNullException.ThrowIfNull(identityVerifier);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);
        _diagnosticSink = diagnosticSink;
        _maxOutputBytes = maxOutputBytes;
        _maxLineBytes = maxLineBytes;
        _cleanupPolicy = cleanupPolicy;
        _identityVerifier = identityVerifier;
        _nativeExitObservationFactory = nativeExitObservationFactory ??
            WindowsNativeExitObservationFactory.Instance;
    }

    public ValueTask<ISupervisedProcess> LaunchAsync(
        LaunchSpecification specification,
        IProcessGroup processGroup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(processGroup);

        var validated = Validate(specification);
        if (processGroup is not WindowsJobObject job)
        {
            throw new ArgumentException(
                "The Windows launcher requires a Windows Job process group.",
                nameof(processGroup));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ISupervisedProcess>(Launch(validated, job, cancellationToken));
    }

    private unsafe WindowsSupervisedProcess Launch(
        ValidatedLaunch validated,
        WindowsJobObject job,
        CancellationToken cancellationToken)
    {
        SafeFileHandle? standardOutputRead = null;
        SafeFileHandle? standardOutputWrite = null;
        SafeFileHandle? standardErrorRead = null;
        SafeFileHandle? standardErrorWrite = null;
        SafeFileHandle? standardInputRead = null;
        SafeFileHandle? standardInputWrite = null;
        SafeProcessHandle? processHandle = null;
        Task? authoritativeNativeExitObservation = null;
        var processInformation = new NativeMethods.ProcessInformation();
        var standardInputTransferred = false;

        try
        {
            (standardOutputRead, standardOutputWrite) = CreatePipe();
            MakeNonInheritable(standardOutputRead);
            (standardErrorRead, standardErrorWrite) = CreatePipe();
            MakeNonInheritable(standardErrorRead);
            (standardInputRead, standardInputWrite) = CreatePipe();
            MakeNonInheritable(standardInputWrite);

            using var jobLease = job.AcquireHandle();
            using var attributeList = StartupAttributeList.Create(attributeCount: 2);
            var jobHandles = stackalloc IntPtr[1] { jobLease.Value };
            attributeList.Add(NativeMethods.ProcThreadAttributeJobList, jobHandles, 1);
            var inheritedHandles = stackalloc IntPtr[3]
            {
                standardInputRead.DangerousGetHandle(),
                standardOutputWrite.DangerousGetHandle(),
                standardErrorWrite.DangerousGetHandle(),
            };
            attributeList.Add(NativeMethods.ProcThreadAttributeHandleList, inheritedHandles, 3);

            var startupInfo = new NativeMethods.StartupInfoEx
            {
                StartupInfo =
                {
                    Size = checked((uint)sizeof(NativeMethods.StartupInfoEx)),
                    Flags = NativeMethods.StartfUseStdHandles,
                    StandardInput = standardInputRead.DangerousGetHandle(),
                    StandardOutput = standardOutputWrite.DangerousGetHandle(),
                    StandardError = standardErrorWrite.DangerousGetHandle(),
                },
                AttributeList = attributeList.Pointer,
            };

            var environment = new NativeEnvironmentBlock(validated.EnvironmentBlock);
            try
            {
                fixed (char* commandLine = validated.CommandLine)
                {
                    if (!NativeMethods.CreateProcess(
                        validated.Specification.ApplicationPath,
                        commandLine,
                        processAttributes: null,
                        threadAttributes: null,
                        inheritHandles: true,
                        NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                        environment.Pointer,
                        validated.Specification.WorkingDirectory,
                        &startupInfo,
                        out processInformation))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                }
            }
            finally
            {
                environment.Dispose();
            }

            processHandle = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            processInformation.Process = IntPtr.Zero;
            authoritativeNativeExitObservation =
                _nativeExitObservationFactory.Create(processHandle);
            using var threadHandle = new SafeKernelObjectHandle(
                processInformation.Thread,
                ownsHandle: true);
            processInformation.Thread = IntPtr.Zero;

            standardInputRead.Dispose();
            standardInputRead = null;
            standardOutputWrite.Dispose();
            standardOutputWrite = null;
            standardErrorWrite.Dispose();
            standardErrorWrite = null;

            cancellationToken.ThrowIfCancellationRequested();
            var identity = _identityVerifier.Capture(
                validated.Specification.Role,
                validated.Specification.Generation,
                processHandle);
            if (!job.Contains(processHandle))
            {
                throw new Win32Exception(87);
            }

            var standardOutput = new BoundedTextCollector(
                _maxOutputBytes,
                _maxLineBytes,
                validated.SecretCanaries);
            var standardError = new BoundedTextCollector(
                _maxOutputBytes,
                _maxLineBytes,
                validated.SecretCanaries);
            if (validated.StandardInput.Length == 0)
            {
                standardInputWrite.Dispose();
                standardInputWrite = null;
            }
            var supervisedProcess = new WindowsSupervisedProcess(
                identity,
                processHandle,
                standardInputWrite,
                validated.StandardInput,
                standardOutputRead,
                standardErrorRead,
                standardOutput,
                standardError,
                validated.Specification.OutputDrainTimeout,
                _diagnosticSink,
                _cleanupPolicy,
                job,
                authoritativeNativeExitObservation);
            processHandle = null;
            standardInputWrite = null;
            standardOutputRead = null;
            standardErrorRead = null;
            standardInputTransferred = true;
            return supervisedProcess;
        }
        catch (OperationCanceledException)
        {
            EnsureCleanupCompleted(
                processHandle,
                job,
                validated.Specification.Role,
                authoritativeNativeExitObservation);
            throw;
        }
        catch (Win32Exception error)
        {
            EnsureCleanupCompleted(
                processHandle,
                job,
                validated.Specification.Role,
                authoritativeNativeExitObservation);
            throw new ProcessLaunchException(validated.Specification.Role, error.NativeErrorCode);
        }
        catch
        {
            EnsureCleanupCompleted(
                processHandle,
                job,
                validated.Specification.Role,
                authoritativeNativeExitObservation);
            throw;
        }
        finally
        {
            if (processInformation.Process != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(processInformation.Process);
            }

            if (processInformation.Thread != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(processInformation.Thread);
            }

            processHandle?.Dispose();
            standardOutputRead?.Dispose();
            standardOutputWrite?.Dispose();
            standardErrorRead?.Dispose();
            standardErrorWrite?.Dispose();
            standardInputRead?.Dispose();
            standardInputWrite?.Dispose();
            if (!standardInputTransferred)
            {
                CryptographicOperations.ZeroMemory(validated.StandardInput);
            }
            CryptographicOperations.ZeroMemory(
                MemoryMarshal.AsBytes(validated.CommandLine.AsSpan()));
        }
    }

    private static ValidatedLaunch Validate(LaunchSpecification specification)
    {
        if (!Path.IsPathFullyQualified(specification.ApplicationPath))
        {
            throw new ArgumentException("The application path must be absolute.", nameof(specification));
        }

        if (!File.Exists(specification.ApplicationPath))
        {
            throw new ArgumentException("The application executable does not exist.", nameof(specification));
        }

        if (!Path.IsPathFullyQualified(specification.WorkingDirectory))
        {
            throw new ArgumentException("The working directory must be absolute.", nameof(specification));
        }

        if (!Directory.Exists(specification.WorkingDirectory))
        {
            throw new ArgumentException("The working directory does not exist.", nameof(specification));
        }

        ArgumentNullException.ThrowIfNull(specification.Arguments);
        ArgumentNullException.ThrowIfNull(specification.Environment);
        ArgumentNullException.ThrowIfNull(specification.SecretEnvironment);
        if (specification.OutputDrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(specification),
                "The output drain timeout must be positive.");
        }

        if (specification.StandardInput.Length > MaximumStandardInputBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(specification),
                $"Standard input cannot exceed {MaximumStandardInputBytes} bytes.");
        }

        foreach (var argument in specification.Arguments)
        {
            if (argument is null || argument.Contains('\0'))
            {
                throw new ArgumentException("Process arguments cannot be null or contain NUL.", nameof(specification));
            }
        }

        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in specification.Environment)
        {
            ValidateEnvironmentEntry(pair.Key, pair.Value);
            if (!environment.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException("Environment keys must be unique ignoring case.", nameof(specification));
            }
        }

        var secretCanaries = new List<string>(specification.SecretEnvironment.Count);
        foreach (var pair in specification.SecretEnvironment)
        {
            ArgumentNullException.ThrowIfNull(pair.Value);
            var value = pair.Value.RevealForChildEnvironment();
            ValidateEnvironmentEntry(pair.Key, value);
            if (!environment.TryAdd(pair.Key, value))
            {
                throw new ArgumentException("Environment keys must be unique ignoring case.", nameof(specification));
            }

            secretCanaries.Add(value);
        }

        return new ValidatedLaunch(
            specification,
            AppendNullTerminator(WindowsCommandLine.Build(
                specification.ApplicationPath,
                specification.Arguments)),
            BuildEnvironmentBlock(environment),
            secretCanaries.ToArray(),
            specification.StandardInput.ToArray());
    }

    private static void ValidateEnvironmentEntry(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || key.Contains('=') || key.Contains('\0'))
        {
            throw new ArgumentException("Environment keys cannot be empty or contain '=' or NUL.");
        }

        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Environment values cannot contain NUL.");
        }
    }

    private static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var length = environment.Sum(pair => checked(pair.Key.Length + pair.Value.Length + 2));
        var block = new char[Math.Max(2, checked(length + 1))];
        var offset = 0;
        foreach (var pair in environment)
        {
            pair.Key.AsSpan().CopyTo(block.AsSpan(offset));
            offset += pair.Key.Length;
            block[offset++] = '=';
            pair.Value.AsSpan().CopyTo(block.AsSpan(offset));
            offset += pair.Value.Length;
            block[offset++] = '\0';
        }

        block[offset] = '\0';
        return block;
    }

    private static char[] AppendNullTerminator(string value)
    {
        var characters = new char[value.Length + 1];
        value.AsSpan().CopyTo(characters);
        return characters;
    }

    private static unsafe (SafeFileHandle Read, SafeFileHandle Write) CreatePipe()
    {
        var attributes = new NativeMethods.SecurityAttributes
        {
            Length = checked((uint)sizeof(NativeMethods.SecurityAttributes)),
            InheritHandle = 1,
        };
        if (!NativeMethods.CreatePipe(out var read, out var write, &attributes, size: 0))
        {
            var error = Marshal.GetLastPInvokeError();
            read?.Dispose();
            write?.Dispose();
            throw new Win32Exception(error);
        }

        return (read, write);
    }

    private static void MakeNonInheritable(SafeFileHandle handle)
    {
        if (!NativeMethods.SetHandleInformation(
            handle,
            NativeMethods.HandleFlagInherit,
            flags: 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private void EnsureCleanupCompleted(
        SafeProcessHandle? processHandle,
        WindowsJobObject job,
        HowardLab.EbayCrm.AppHost.Protocol.Control.RuntimeRole role,
        Task? authoritativeNativeExitObservation)
    {
        if (processHandle is null || processHandle.IsInvalid || processHandle.IsClosed)
        {
            return;
        }

        var result = _cleanupPolicy.Cleanup(processHandle, job);
        if (!result.Signaled)
        {
            var observationKind = NativeExitObservationKind.Authoritative;
            if (authoritativeNativeExitObservation is null)
            {
                try
                {
                    authoritativeNativeExitObservation =
                        _nativeExitObservationFactory.Create(processHandle);
                }
                catch
                {
                    authoritativeNativeExitObservation = ProofUnavailableContainment;
                    observationKind = NativeExitObservationKind.ProofUnavailableContainment;
                }
            }

            throw new ProcessCleanupException(
                role,
                result.ProcessTerminationErrorCode,
                result.JobTerminationErrorCode,
                result.WaitErrorCode,
                result.TimedOut,
                authoritativeNativeExitObservation,
                observationKind);
        }
    }

    private sealed record ValidatedLaunch(
        LaunchSpecification Specification,
        char[] CommandLine,
        char[] EnvironmentBlock,
        string[] SecretCanaries,
        byte[] StandardInput);

    private sealed unsafe class NativeEnvironmentBlock : IDisposable
    {
        private void* _pointer;
        private readonly nuint _byteLength;

        internal NativeEnvironmentBlock(char[] characters)
        {
            _byteLength = checked((nuint)(characters.Length * sizeof(char)));
            _pointer = NativeMemory.Alloc(_byteLength);
            fixed (char* source = characters)
            {
                Buffer.MemoryCopy(source, _pointer, _byteLength, _byteLength);
            }

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
        }

        internal void* Pointer => _pointer;

        public void Dispose()
        {
            var pointer = _pointer;
            if (pointer is null)
            {
                return;
            }

            _pointer = null;
            CryptographicOperations.ZeroMemory(new Span<byte>(pointer, checked((int)_byteLength)));
            NativeMemory.Free(pointer);
        }
    }
}
