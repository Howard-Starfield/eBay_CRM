using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Processes;

public sealed class WindowsSupervisedProcess : ISupervisedProcess
{
    private readonly FileStream _standardOutputStream;
    private readonly FileStream _standardErrorStream;
    private readonly FileStream? _standardInputStream;
    private readonly CancellationTokenSource _drainCancellation = new();
    private readonly CancellationTokenSource _completionCancellation = new();
    private readonly TimeSpan _outputDrainTimeout;
    private readonly IDiagnosticSink _diagnosticSink;
    private readonly Task _standardOutputDrain;
    private readonly Task _standardErrorDrain;
    private readonly Task _standardInputWrite;
    private readonly DrainState _standardOutputState;
    private readonly DrainState _standardErrorState;
    private readonly IProcessCleanupPolicy _cleanupPolicy;
    private readonly IProcessTreeTerminator _job;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _resourcesTerminalized;
    private int _forceCloseStarted;

    internal WindowsSupervisedProcess(
        SupervisedProcessIdentity identity,
        SafeProcessHandle processHandle,
        SafeFileHandle? standardInputWrite,
        byte[] standardInput,
        SafeFileHandle standardOutputRead,
        SafeFileHandle standardErrorRead,
        BoundedTextCollector standardOutput,
        BoundedTextCollector standardError,
        TimeSpan outputDrainTimeout,
        IDiagnosticSink diagnosticSink,
        IProcessCleanupPolicy cleanupPolicy,
        IProcessTreeTerminator job)
    {
        Identity = identity;
        ProcessHandle = processHandle;
        NativeExitObservation = WaitForDuplicatedProcessExitAsync(
            DuplicateProcessHandle(processHandle),
            CancellationToken.None);
        StandardOutput = standardOutput;
        StandardError = standardError;
        _outputDrainTimeout = outputDrainTimeout;
        _diagnosticSink = diagnosticSink;
        _cleanupPolicy = cleanupPolicy;
        _job = job;
        _standardOutputStream = new FileStream(
            standardOutputRead,
            FileAccess.Read,
            bufferSize: 4096,
            isAsync: false);
        _standardErrorStream = new FileStream(
            standardErrorRead,
            FileAccess.Read,
            bufferSize: 4096,
            isAsync: false);
        _standardInputStream = standardInputWrite is null
            ? null
            : new FileStream(standardInputWrite, FileAccess.Write, bufferSize: 4096, isAsync: false);
        _standardOutputState = new DrainState(StandardOutput);
        _standardErrorState = new DrainState(StandardError);
        _standardOutputDrain = DrainAsync(
            _standardOutputStream,
            _standardOutputState,
            _drainCancellation.Token);
        _standardErrorDrain = DrainAsync(
            _standardErrorStream,
            _standardErrorState,
            _drainCancellation.Token);
        _standardInputWrite = WriteStandardInputAsync(_standardInputStream, standardInput);
        Completion = CompleteAsync(_completionCancellation.Token);
    }

    public SupervisedProcessIdentity Identity { get; }

    internal SafeProcessHandle ProcessHandle { get; }

    internal Task NativeExitObservation { get; }

    public Task<int> Completion { get; }

    public BoundedTextCollector StandardOutput { get; }

    public BoundedTextCollector StandardError { get; }

    internal Task StandardOutputLineAvailable => _standardOutputState.LineAvailable;

    public bool HasExited
    {
        get
        {
            using var process = DuplicateRetainedProcessHandle();
            if (process is null)
            {
                return true;
            }

            var result = NativeMethods.WaitForSingleObject(process, milliseconds: 0);
            return result switch
            {
                NativeMethods.WaitObject0 => true,
                NativeMethods.WaitTimeout => false,
                _ => throw new Win32Exception(Marshal.GetLastPInvokeError()),
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    internal void ForceCloseAfterJobClose()
        => ForceCloseAfterJobClose(CancellationToken.None);

    internal void ForceCloseAfterJobClose(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _forceCloseStarted, 1) != 0)
        {
            return;
        }

        lock (_disposeGate)
        {
            _disposeTask ??= Task.CompletedTask;
        }

        TerminalizeResources();
        try
        {
            Completion.WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void TerminateAndForceCloseAfterJobClose(CancellationToken cancellationToken)
    {
        using var process = DuplicateRetainedProcessHandle();
        if (process is null)
        {
            ForceCloseAfterJobClose(cancellationToken);
            return;
        }

        try
        {
            var wait = NativeMethods.WaitForSingleObject(process, milliseconds: 0);
            if (wait == NativeMethods.WaitTimeout)
            {
                if (!NativeMethods.TerminateProcess(process, exitCode: 1) &&
                    NativeMethods.WaitForSingleObject(process, milliseconds: 0) != NativeMethods.WaitObject0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                using var processWait = new ProcessWaitHandle(process);
                _ = WaitHandle.WaitAny([processWait, cancellationToken.WaitHandle]);
            }
            else if (wait != NativeMethods.WaitObject0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            ForceCloseAfterJobClose(cancellationToken);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (!HasExited)
            {
                using var cleanupHandle = DuplicateRetainedProcessHandle();
                if (cleanupHandle is null)
                {
                    return;
                }

                var cleanup = await Task.Run(
                    () => _cleanupPolicy.Cleanup(cleanupHandle, _job)).ConfigureAwait(false);
                if (!cleanup.Signaled)
                {
                    throw new ProcessCleanupException(
                        Identity.Role,
                        cleanup.ProcessTerminationErrorCode,
                        cleanup.JobTerminationErrorCode,
                        cleanup.WaitErrorCode,
                        cleanup.TimedOut);
                }
            }

            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _forceCloseStarted) != 0)
            {
            }
            await _standardInputWrite.ConfigureAwait(false);
        }
        finally
        {
            TerminalizeResources();
        }
    }

    private void TerminalizeResources()
    {
        if (Interlocked.Exchange(ref _resourcesTerminalized, 1) != 0)
        {
            return;
        }

        _completionCancellation.Cancel();
        _drainCancellation.Cancel();
        _standardOutputStream.Dispose();
        _standardErrorStream.Dispose();
        _standardInputStream?.Dispose();
        ProcessHandle.Dispose();
        _ = DisposeCancellationSourcesWhenQuiescentAsync();
    }

    private async Task DisposeCancellationSourcesWhenQuiescentAsync()
    {
        try
        {
            await Task.WhenAll(
                Completion,
                _standardOutputDrain,
                _standardErrorDrain,
                _standardInputWrite).ConfigureAwait(false);
        }
        catch
        {
            // Terminalization owns the task outcomes; cancellation and closed pipes are expected here.
        }
        finally
        {
            _drainCancellation.Dispose();
            _completionCancellation.Dispose();
        }
    }

    private SafeProcessHandle? DuplicateRetainedProcessHandle()
    {
        var lease = false;
        try
        {
            ProcessHandle.DangerousAddRef(ref lease);
            if (!lease || ProcessHandle.IsClosed || ProcessHandle.IsInvalid)
            {
                return null;
            }

            var currentProcess = NativeMethods.GetCurrentProcess();
            if (!NativeMethods.DuplicateProcessHandle(
                currentProcess,
                ProcessHandle,
                currentProcess,
                out var duplicate,
                desiredAccess: 0,
                inheritHandle: false,
                NativeMethods.DuplicateSameAccess))
            {
                if (Volatile.Read(ref _resourcesTerminalized) != 0)
                {
                    return null;
                }

                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new SafeProcessHandle(duplicate, ownsHandle: true);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _resourcesTerminalized) != 0)
        {
            return null;
        }
        finally
        {
            if (lease)
            {
                ProcessHandle.DangerousRelease();
            }
        }
    }

    private static async Task WriteStandardInputAsync(FileStream? stream, byte[] input)
    {
        try
        {
            if (stream is not null)
            {
                await stream.WriteAsync(input).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The child may exit before consuming all bounded input.
        }
        catch (ObjectDisposedException)
        {
            // Disposal is the cancellation boundary for a blocked pipe write.
        }
        finally
        {
            stream?.Dispose();
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private async Task<int> CompleteAsync(CancellationToken cancellationToken)
    {
        using var completionHandle = DuplicateProcessHandle(ProcessHandle);
        await WaitForExitAsync(completionHandle, cancellationToken).ConfigureAwait(false);
        if (!NativeMethods.GetExitCodeProcess(completionHandle, out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var drains = Task.WhenAll(_standardOutputDrain, _standardErrorDrain);
        try
        {
            await drains.WaitAsync(_outputDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _standardOutputState.Stop();
            _standardErrorState.Stop();
            _drainCancellation.Cancel();
            _standardOutputStream.Dispose();
            _standardErrorStream.Dispose();
            try
            {
                await drains.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Collectors are terminal; a stuck OS read cannot delay lifecycle indefinitely.
            }

            _ = Task.Run(WriteDrainTimeoutDiagnosticAsync);
        }

        return unchecked((int)exitCode);
    }

    private async Task WriteDrainTimeoutDiagnosticAsync()
    {
        try
        {
            var diagnosticEvent = DiagnosticEvent.Create("process.output_drain_timeout")
                .With("role", DiagnosticField.String(Identity.Role.ToString()))
                .With("process_id", DiagnosticField.Integer(Identity.ProcessId));
            await _diagnosticSink.WriteAsync(diagnosticEvent).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must never impede process lifecycle completion.
        }
    }

    private static async Task DrainAsync(
        FileStream stream,
        DrainState state,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    return;
                }

                state.Append(buffer.AsSpan(0, bytesRead));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            state.Stop();
            stream.Dispose();
        }
    }

    private sealed class DrainState
    {
        private readonly object _gate = new();
        private readonly BoundedTextCollector _collector;
        private readonly TaskCompletionSource _lineAvailable =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _stopped;

        internal DrainState(BoundedTextCollector collector)
        {
            _collector = collector;
        }

        internal Task LineAvailable => _lineAvailable.Task;

        internal void Append(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                if (!_stopped)
                {
                    _collector.Append(bytes);
                    if (_collector.LineCount > 0)
                    {
                        _lineAvailable.TrySetResult();
                    }
                }
            }
        }

        internal void Stop()
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _collector.Complete();
            }
        }
    }

    private static Task WaitForExitAsync(
        SafeProcessHandle processHandle,
        CancellationToken cancellationToken)
    {
        return WaitForDuplicatedProcessExitAsync(
            DuplicateProcessHandle(processHandle),
            cancellationToken);
    }

    private static Task WaitForDuplicatedProcessExitAsync(
        SafeProcessHandle duplicatedHandle,
        CancellationToken cancellationToken)
    {
        ProcessWaitHandle? waitHandle = null;
        RegisteredWaitHandle? registration = null;
        CancellationTokenRegistration cancellationRegistration = default;
        try
        {
            waitHandle = new ProcessWaitHandle(duplicatedHandle);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            registration = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
                completion,
                Timeout.Infinite,
                executeOnlyOnce: true);
            cancellationRegistration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            return AwaitAndDisposeAsync(
                completion.Task,
                registration,
                waitHandle,
                cancellationRegistration,
                duplicatedHandle);
        }
        catch
        {
            cancellationRegistration.Dispose();
            registration?.Unregister(waitObject: null);
            waitHandle?.Dispose();
            duplicatedHandle.Dispose();
            throw;
        }
    }

    private static SafeProcessHandle DuplicateProcessHandle(SafeProcessHandle processHandle)
    {
        var currentProcess = NativeMethods.GetCurrentProcess();
        if (!NativeMethods.DuplicateProcessHandle(
            currentProcess,
            processHandle,
            currentProcess,
            out var duplicatedValue,
            desiredAccess: 0,
            inheritHandle: false,
            NativeMethods.DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new SafeProcessHandle(duplicatedValue, ownsHandle: true);
    }

    private static async Task AwaitAndDisposeAsync(
        Task completion,
        RegisteredWaitHandle registration,
        WaitHandle waitHandle,
        CancellationTokenRegistration cancellationRegistration,
        SafeProcessHandle duplicatedHandle)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
            registration.Unregister(waitObject: null);
            waitHandle.Dispose();
            duplicatedHandle.Dispose();
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeProcessHandle processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(
                processHandle.DangerousGetHandle(),
                ownsHandle: false);
        }
    }
}
