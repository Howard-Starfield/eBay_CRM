using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Instance;

public sealed class UserProfileInstanceLock : IInstanceLock
{
    private readonly ManualResetEventSlim _release;
    private readonly Task _ownerCompletion;
    private int _disposed;

    private UserProfileInstanceLock(
        DataProfileIdentity identity,
        ManualResetEventSlim release,
        Task ownerCompletion)
    {
        Identity = identity;
        _release = release;
        _ownerCompletion = ownerCompletion;
    }

    public DataProfileIdentity Identity { get; }

    public static ValueTask<UserProfileInstanceLock?> TryAcquireAsync(
        DataProfileIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<UserProfileInstanceLock?>(cancellationToken);
        }

        var acquisition = new Acquisition(identity, cancellationToken);
        var thread = new Thread(acquisition.Run)
        {
            IsBackground = true,
            Name = "AppHost profile ownership",
        };
        thread.Start();
        return new ValueTask<UserProfileInstanceLock?>(acquisition.Result.Task);
    }

    internal static string BuildMutexName(DataProfileIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var userSid = NativeSecurityDescriptor.GetCurrentUserSid().ToUpperInvariant();
        var userHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(userSid)));
        return $"Global\\HowardLab.EbayCrm.AppHost.{userHash}.{identity.ProfileHash}";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        await _ownerCompletion.ConfigureAwait(false);
    }

    private sealed class Acquisition
    {
        private readonly DataProfileIdentity _identity;
        private readonly CancellationToken _cancellationToken;
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly TaskCompletionSource _ownerCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Acquisition(DataProfileIdentity identity, CancellationToken cancellationToken)
        {
            _identity = identity;
            _cancellationToken = cancellationToken;
        }

        internal TaskCompletionSource<UserProfileInstanceLock?> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal unsafe void Run()
        {
            SafeWaitHandle? mutex = null;
            FileStream? lockFile = null;
            var ownsMutex = false;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                using var securityDescriptor = NativeSecurityDescriptor.CreateForCurrentUserOnly();
                var attributes = new NativeMethods.SecurityAttributes
                {
                    Length = checked((uint)sizeof(NativeMethods.SecurityAttributes)),
                    SecurityDescriptor = securityDescriptor.DangerousGetHandle().ToPointer(),
                    InheritHandle = 0,
                };
                var mutexName = UserProfileInstanceLock.BuildMutexName(_identity);
                mutex = NativeMethods.CreateMutexEx(
                    &attributes,
                    mutexName,
                    flags: 0,
                    NativeMethods.MutexAllAccess);
                if (mutex.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                var wait = NativeMethods.WaitForSingleObjectHandle(mutex, milliseconds: 0);
                if (wait == NativeMethods.WaitTimeout)
                {
                    Result.TrySetResult(null);
                    return;
                }

                if (wait is not (NativeMethods.WaitObject0 or NativeMethods.WaitAbandoned0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                ownsMutex = true;
                _cancellationToken.ThrowIfCancellationRequested();
                var runtimeDirectory = Path.Combine(_identity.CanonicalPath, "runtime");
                Directory.CreateDirectory(runtimeDirectory);
                try
                {
                    lockFile = new FileStream(
                        Path.Combine(runtimeDirectory, "profile.lock"),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException error) when (IsSharingViolation(error))
                {
                    Result.TrySetResult(null);
                    return;
                }

                WriteDiagnosticContents(lockFile, _identity.ProfileHash);
                var instanceLock = new UserProfileInstanceLock(
                    _identity,
                    _release,
                    _ownerCompletion.Task);
                Result.TrySetResult(instanceLock);
                _release.Wait();
            }
            catch (OperationCanceledException)
            {
                Result.TrySetCanceled(_cancellationToken);
            }
            catch (Exception error)
            {
                Result.TrySetException(error);
            }
            finally
            {
                lockFile?.Dispose();
                if (ownsMutex && mutex is not null && !mutex.IsInvalid)
                {
                    _ = NativeMethods.ReleaseMutex(mutex);
                }

                mutex?.Dispose();
                _ownerCompletion.TrySetResult();
                _release.Dispose();
            }
        }

        private static void WriteDiagnosticContents(FileStream lockFile, string profileHash)
        {
            lockFile.SetLength(0);
            lockFile.Position = 0;
            using var process = Process.GetCurrentProcess();
            JsonSerializer.Serialize(lockFile, new
            {
                ProcessId = Environment.ProcessId,
                ProcessCreationTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                ProfileHash = profileHash,
            });
            lockFile.Flush(flushToDisk: true);
        }

        private static bool IsSharingViolation(IOException error)
        {
            var win32Error = error.HResult & 0xffff;
            return win32Error is 32 or 33;
        }
    }
}
