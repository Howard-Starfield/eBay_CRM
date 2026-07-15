using System.ComponentModel;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Postgres;

public sealed class PostgresInstanceIdentity : IDisposable
{
    internal PostgresInstanceIdentity(
        ProcessGeneration generation,
        string canonicalDataDirectory,
        int processId,
        SafeProcessHandle postmasterHandle,
        DateTimeOffset creationTimeUtc,
        string verifiedImagePath,
        bool verifiedJobMembership,
        int loopbackPort,
        Guid? clusterId)
    {
        Generation = generation;
        CanonicalDataDirectory = canonicalDataDirectory;
        ProcessId = processId;
        PostmasterHandle = postmasterHandle;
        CreationTimeUtc = creationTimeUtc;
        VerifiedImagePath = verifiedImagePath;
        VerifiedJobMembership = verifiedJobMembership;
        LoopbackPort = loopbackPort;
        ClusterId = clusterId;
    }

    public ProcessGeneration Generation { get; }
    public string CanonicalDataDirectory { get; }
    public int ProcessId { get; }
    public SafeProcessHandle PostmasterHandle { get; }
    public DateTimeOffset CreationTimeUtc { get; }
    public string VerifiedImagePath { get; }
    public bool VerifiedJobMembership { get; }
    public int LoopbackPort { get; }
    public Guid? ClusterId { get; }

    public bool HasExited
    {
        get
        {
            if (PostmasterHandle.IsInvalid || PostmasterHandle.IsClosed)
                throw new ObjectDisposedException(nameof(PostgresInstanceIdentity));
            var result = NativeMethods.WaitForSingleObject(PostmasterHandle, 0);
            return result switch
            {
                NativeMethods.WaitObject0 => true,
                NativeMethods.WaitTimeout => false,
                _ => throw new Win32Exception(Marshal.GetLastPInvokeError()),
            };
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (PostmasterHandle.IsInvalid || PostmasterHandle.IsClosed) throw new ObjectDisposedException(nameof(PostgresInstanceIdentity));
        var waitHandle = new ProcessWaitHandle(PostmasterHandle);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            Timeout.Infinite,
            executeOnlyOnce: true);
        var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return AwaitAndDisposeAsync(completion.Task, registration, waitHandle, cancellation);
    }

    public void Dispose() => PostmasterHandle.Dispose();

    private static async Task AwaitAndDisposeAsync(
        Task completion,
        RegisteredWaitHandle registration,
        WaitHandle waitHandle,
        CancellationTokenRegistration cancellation)
    {
        try { await completion.ConfigureAwait(false); }
        finally
        {
            cancellation.Dispose();
            registration.Unregister(null);
            waitHandle.Dispose();
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeProcessHandle handle) =>
            SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), ownsHandle: false);
    }
}
