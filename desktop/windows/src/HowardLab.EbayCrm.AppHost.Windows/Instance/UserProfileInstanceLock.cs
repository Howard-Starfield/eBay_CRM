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

internal interface IProfileLockFileSystem
{
    void CreateDirectory(string path, CancellationToken cancellationToken);

    FileStream OpenLockFile(string path, CancellationToken cancellationToken);

    void ValidateProfilePath(string path, CancellationToken cancellationToken);

    void WriteDiagnostic(
        FileStream lockFile,
        string profileHash,
        CancellationToken cancellationToken);
}

internal sealed class WindowsProfileLockFileSystem : IProfileLockFileSystem
{
    internal static WindowsProfileLockFileSystem Instance { get; } = new();

    private WindowsProfileLockFileSystem()
    {
    }

    public void CreateDirectory(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public FileStream OpenLockFile(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        if (cancellationToken.IsCancellationRequested)
        {
            stream.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return stream;
    }

    public void ValidateProfilePath(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataProfileIdentity.EnsureNoReparsePoints(path, WindowsProfilePathInspector.Instance);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void WriteDiagnostic(
        FileStream lockFile,
        string profileHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lockFile.SetLength(0);
        lockFile.Position = 0;
        using var process = Process.GetCurrentProcess();
        JsonSerializer.Serialize(lockFile, new
        {
            ProcessId = Environment.ProcessId,
            ProcessCreationTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            ProfileHash = profileHash,
        });
        cancellationToken.ThrowIfCancellationRequested();
        lockFile.Flush();
        cancellationToken.ThrowIfCancellationRequested();
    }
}

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
        CancellationToken cancellationToken) =>
        TryAcquireAsync(identity, cancellationToken, WindowsProfileLockFileSystem.Instance);

    internal static ValueTask<UserProfileInstanceLock?> TryAcquireAsync(
        DataProfileIdentity identity,
        CancellationToken cancellationToken,
        IProfileLockFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<UserProfileInstanceLock?>(cancellationToken);
        }

        var acquisition = new Acquisition(identity, cancellationToken, fileSystem);
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
        private readonly IProfileLockFileSystem _fileSystem;
        private readonly object _publicationGate = new();
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly TaskCompletionSource _ownerCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _cancellationObserved;

        internal Acquisition(
            DataProfileIdentity identity,
            CancellationToken cancellationToken,
            IProfileLockFileSystem fileSystem)
        {
            _identity = identity;
            _cancellationToken = cancellationToken;
            _fileSystem = fileSystem;
        }

        internal TaskCompletionSource<UserProfileInstanceLock?> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal unsafe void Run()
        {
            SafeWaitHandle? mutex = null;
            FileStream? lockFile = null;
            var ownsMutex = false;
            var published = false;
            var canceled = false;
            var notAcquired = false;
            Exception? failure = null;
            using var cancellationRegistration = _cancellationToken.Register(
                static state => ((Acquisition)state!).ObserveCancellation(),
                this);
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
                var creationError = Marshal.GetLastPInvokeError();
                if (mutex.IsInvalid)
                {
                    if (creationError == NativeMethods.ErrorAccessDenied)
                    {
                        throw new ProfileOwnershipException(
                            ProfileOwnershipErrorCode.ProfileMutexSecurityMismatch);
                    }

                    throw new Win32Exception(creationError);
                }

                if (creationError is not (0 or NativeMethods.ErrorAlreadyExists))
                {
                    throw new Win32Exception(creationError);
                }

                ValidateMutexSecurity(mutex);

                var wait = NativeMethods.WaitForSingleObjectHandle(mutex, milliseconds: 0);
                if (wait == NativeMethods.WaitTimeout)
                {
                    notAcquired = true;
                }
                else if (wait is not (NativeMethods.WaitObject0 or NativeMethods.WaitAbandoned0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                else
                {
                    ownsMutex = true;
                    _cancellationToken.ThrowIfCancellationRequested();
                    var runtimeDirectory = Path.Combine(_identity.CanonicalPath, "runtime");
                    _fileSystem.CreateDirectory(runtimeDirectory, _cancellationToken);
                    _cancellationToken.ThrowIfCancellationRequested();
                    _fileSystem.ValidateProfilePath(_identity.CanonicalPath, _cancellationToken);
                    _cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        lockFile = _fileSystem.OpenLockFile(
                            Path.Combine(runtimeDirectory, "profile.lock"),
                            _cancellationToken);
                        _cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (IOException error) when (IsSharingViolation(error))
                    {
                        notAcquired = true;
                    }

                    if (!notAcquired)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        _fileSystem.WriteDiagnostic(
                            lockFile!,
                            _identity.ProfileHash,
                            _cancellationToken);
                        _cancellationToken.ThrowIfCancellationRequested();
                        var instanceLock = new UserProfileInstanceLock(
                            _identity,
                            _release,
                            _ownerCompletion.Task);
                        Publish(instanceLock);
                        published = true;
                        _release.Wait();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            catch (Exception error)
            {
                failure = error;
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

            if (!published)
            {
                if (canceled)
                {
                    Result.TrySetCanceled(_cancellationToken);
                }
                else if (failure is not null)
                {
                    Result.TrySetException(failure);
                }
                else
                {
                    Result.TrySetResult(null);
                }
            }
        }

        private void ObserveCancellation()
        {
            lock (_publicationGate)
            {
                _cancellationObserved = true;
            }
        }

        private static void ValidateMutexSecurity(SafeWaitHandle mutex)
        {
            try
            {
                if (!NativeMutexSecurity.IsCurrentUserOnly(mutex))
                {
                    throw new ProfileOwnershipException(
                        ProfileOwnershipErrorCode.ProfileMutexSecurityMismatch);
                }
            }
            catch (ProfileOwnershipException)
            {
                throw;
            }
            catch (Exception error) when (error is Win32Exception or ArgumentException or OverflowException)
            {
                throw new ProfileOwnershipException(
                    ProfileOwnershipErrorCode.ProfileMutexSecurityMismatch);
            }
        }

        private void Publish(UserProfileInstanceLock instanceLock)
        {
            lock (_publicationGate)
            {
                if (_cancellationObserved || _cancellationToken.IsCancellationRequested)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(_cancellationToken);
                }

                Result.TrySetResult(instanceLock);
            }
        }

        private static bool IsSharingViolation(IOException error)
        {
            var win32Error = error.HResult & 0xffff;
            return win32Error is 32 or 33;
        }
    }
}
