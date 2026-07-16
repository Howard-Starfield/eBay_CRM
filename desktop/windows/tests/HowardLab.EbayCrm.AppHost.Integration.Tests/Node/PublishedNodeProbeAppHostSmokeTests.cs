using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Payload;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Node;

[Collection("Published Node probe acceptance")]
public sealed partial class PublishedNodeProbeAppHostSmokeTests(ITestOutputHelper output)
{
    [PostgresFact, Trait("Category", "PublishedNodeProbe")]
    public async Task PublishedPayload_ExternallyCompletesReadyThenStoppedAndLeavesNoProcessOrProfile()
    {
        var publishRoot = TestLayout.FindPublishedDirectory();
        var payloadRoot = Path.Combine(publishRoot, "node-probe");
        ValidatePayload(payloadRoot);

        var layout = TestLayout.CreatePublished("ebaycrm-published-node-probe");
        WindowsJobObject? auditJob = null;
        JobCompletionProcessAudit? processAudit = null;
        ISupervisedProcess? host = null;
        Exception? runFailure = null;
        try
        {
            auditJob = WindowsJobObject.CreateKillOnClose();
            processAudit = JobCompletionProcessAudit.Create(auditJob);
            host = await StartAsync(
                Path.Combine(publishRoot, "HowardLab.EbayCrm.AppHost.exe"),
                publishRoot,
                layout,
                payloadRoot,
                auditJob);

            var exitCode = await host.Completion.WaitAsync(TimeSpan.FromMinutes(3));
            await processAudit.WaitForActiveProcessZeroAsync(TimeSpan.FromSeconds(15));
            var stdout = host.StandardOutput.Snapshot();
            var stderr = host.StandardError.Snapshot();
            Assert.True(exitCode == 0,
                $"Published AppHost exit={exitCode}; stderr={stderr}; stdout={stdout}");
            var states = stdout.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ready = Array.FindIndex(states, line =>
                StringComparer.Ordinal.Equals(line, RuntimeState.Ready.ToString()));
            var stopped = Array.FindIndex(states, line =>
                StringComparer.Ordinal.Equals(line, RuntimeState.Stopped.ToString()));
            Assert.True(ready >= 0, $"Ready was not reported. stdout={stdout}");
            Assert.True(stopped > ready, $"Stopped was not reported after Ready. stdout={stdout}");

            var identities = processAudit.Snapshot();
            var accounting = auditJob.GetAccountingSnapshot();
            Assert.Equal(0u, accounting.ActiveProcesses);
            Assert.Equal(checked((int)accounting.TotalProcesses), identities.Count);
            var hostIdentity = Assert.Single(identities, identity =>
                identity.ProcessId == host.Identity.ProcessId &&
                identity.CreationTimeUtc == host.Identity.CreationTimeUtc &&
                identity.ImageName.Equals(
                    "HowardLab.EbayCrm.AppHost.exe",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, identities.Count(identity =>
                identity.ImageName.Equals("node.exe", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(identities, identity =>
                identity.ImageName.Equals("postgres.exe", StringComparison.OrdinalIgnoreCase));
            Assert.All(identities, identity =>
            {
                Assert.True(identity.ParentProcessId > 0);
                Assert.NotNull(identity.ParentCreationTimeUtc);
                Assert.True(identity.ExitObserved,
                    $"The audit did not observe process exit: {identity}");
            });
            Assert.Equal(
                identities.Count,
                identities.Select(identity => (identity.ProcessId, identity.CreationTimeUtc)).Distinct().Count());
            var identityKeys = identities
                .Select(identity => (identity.ProcessId, identity.CreationTimeUtc))
                .ToHashSet();
            foreach (var identity in identities)
            {
                Assert.NotNull(identity.ParentCreationTimeUtc);
                var parentKey = (identity.ParentProcessId, identity.ParentCreationTimeUtc.Value);
                if (ReferenceEquals(identity, hostIdentity))
                {
                    Assert.DoesNotContain(parentKey, identityKeys);
                    continue;
                }

                Assert.Contains(parentKey, identityKeys);
                Assert.True(identity.ParentCreationTimeUtc.Value <= identity.CreationTimeUtc,
                    $"Parent creation followed child creation: {identity}");
            }
            Assert.False(File.Exists(Path.Combine(layout.ProfileRoot, "postgres-data", "postmaster.pid")));

            output.WriteLine(JsonSerializer.Serialize(
                identities.OrderBy(identity => identity.CreationTimeUtc),
                new JsonSerializerOptions { WriteIndented = true }));
            output.WriteLine($"Published state output: {string.Join(" -> ", states)}");
        }
        catch (Exception error)
        {
            runFailure = error;
        }

        var cleanupFailures = await CleanupAsync(host, auditJob, processAudit, layout);
        if (runFailure is not null && cleanupFailures.Count != 0)
        {
            throw new AggregateException(
                "Published Node probe execution and cleanup both failed.",
                [runFailure, .. cleanupFailures]);
        }

        if (runFailure is not null)
        {
            ExceptionDispatchInfo.Capture(runFailure).Throw();
        }

        if (cleanupFailures.Count != 0)
        {
            throw new AggregateException(
                "Published Node probe cleanup failed.",
                cleanupFailures);
        }

        Assert.False(Directory.Exists(layout.Root));
    }

    private static async Task<IReadOnlyList<Exception>> CleanupAsync(
        ISupervisedProcess? host,
        WindowsJobObject? auditJob,
        JobCompletionProcessAudit? processAudit,
        TestLayout layout)
    {
        var failures = new List<Exception>();
        await AttemptAsync(async () =>
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        });
        Attempt(() => processAudit?.TerminateAuditedJob());
        Attempt(() => auditJob?.Dispose());
        await AttemptAsync(async () =>
        {
            if (processAudit is not null)
            {
                await processAudit.StopAsync(TimeSpan.FromSeconds(15));
            }
        });
        Attempt(() => processAudit?.Dispose());
        Attempt(() =>
        {
            if (Directory.Exists(layout.Root))
            {
                AssertTreeContainsNoReparsePoints(layout.Root);
                layout.Dispose();
            }
        });
        return failures;

        void Attempt(Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        async Task AttemptAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }
    }

    private static void ValidatePayload(string payloadRoot)
    {
        var manifestPath = Path.Combine(payloadRoot, "node-payload-manifest-v1.json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        Assert.False(manifestBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var manifest = NodePayloadManifestV1.Parse(manifestBytes);
        Assert.Equal("published-node-probe/1", manifest.BuildIdentity);
        Assert.Equal("node.exe", manifest.NodeExecutable);
        Assert.Equal("app/probes/server-probe.js", manifest.ServerEntrypoint);
        Assert.Equal("app/probes/worker-probe.js", manifest.WorkerEntrypoint);
        Assert.Equal(
            manifest.Artifacts.Select(artifact => artifact.Path).OrderBy(path => path, StringComparer.Ordinal),
            manifest.Artifacts.Select(artifact => artifact.Path));

        var declared = manifest.Artifacts.ToDictionary(
            artifact => artifact.Path,
            StringComparer.Ordinal);
        Assert.Equal(
            Encoding.UTF8.GetBytes("{\"type\":\"module\"}"),
            File.ReadAllBytes(Path.Combine(payloadRoot, "package.json")));

        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(payloadRoot);
        while (pending.TryDequeue(out var directory))
        {
            Assert.False((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                Assert.False((attributes & FileAttributes.ReparsePoint) != 0);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Enqueue(entry);
                    continue;
                }

                actualFiles.Add(Path.GetRelativePath(payloadRoot, entry).Replace('\\', '/'));
            }
        }

        Assert.Equal(
            declared.Keys.Append("node-payload-manifest-v1.json").OrderBy(path => path, StringComparer.Ordinal),
            actualFiles.OrderBy(path => path, StringComparer.Ordinal));
        foreach (var artifact in manifest.Artifacts)
        {
            var path = Path.Combine(payloadRoot, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
            using var stream = File.OpenRead(path);
            Assert.Equal(artifact.Length, stream.Length);
            Assert.Equal(artifact.Sha256, Convert.ToHexString(SHA256.HashData(stream)));
            if (artifact.Path.StartsWith("app/", StringComparison.Ordinal))
            {
                Assert.EndsWith(".js", artifact.Path, StringComparison.Ordinal);
            }
        }

        foreach (var artifact in manifest.Artifacts.Where(item => item.Path.StartsWith("app/", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(Path.Combine(
                payloadRoot,
                artifact.Path.Replace('/', Path.DirectorySeparatorChar)));
            var containing = artifact.Path[..(artifact.Path.LastIndexOf('/') + 1)];
            foreach (Match match in RelativeImport().Matches(source))
            {
                var imported = Path.GetFullPath(Path.Combine(
                    payloadRoot,
                    containing.Replace('/', Path.DirectorySeparatorChar),
                    match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
                var relative = Path.GetRelativePath(payloadRoot, imported).Replace('\\', '/');
                Assert.True(declared.ContainsKey(relative),
                    $"Imported JavaScript is undeclared: {artifact.Path} -> {relative}");
            }
        }
    }

    private static async Task<ISupervisedProcess> StartAsync(
        string executable,
        string publishRoot,
        TestLayout layout,
        string payloadRoot,
        WindowsJobObject auditJob)
    {
        var arguments = layout.Arguments(
                "acceptance-run-once",
                runtimeBackend: "redis",
                roleTarget: "controlled-node-probe")
            .Concat(["--node-probe-root", payloadRoot])
            .ToArray();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EBAYCRM_RELEASE_ACCEPTANCE"] = "1",
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ??
                throw new InvalidOperationException("SystemRoot is unavailable."),
        };
        var launcher = new WindowsProcessLauncher(
            NoopDiagnosticSink.Instance,
            maxOutputBytes: 1024 * 1024,
            maxLineBytes: 64 * 1024,
            processCleanupTimeout: TimeSpan.FromSeconds(15));
        return await launcher.LaunchAsync(
            new LaunchSpecification(
                RuntimeRole.Server,
                new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid()),
                executable,
                arguments,
                publishRoot,
                environment,
                new Dictionary<string, SecretValue>(),
                TimeSpan.FromSeconds(5)),
            auditJob,
            CancellationToken.None);
    }

    private static void AssertTreeContainsNoReparsePoints(string root)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var directory))
        {
            Assert.False((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0,
                $"Profile cleanup refused a reparse-point directory: {directory}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                Assert.False((attributes & FileAttributes.ReparsePoint) != 0,
                    $"Profile cleanup refused a reparse-point entry: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Enqueue(entry);
                }
            }
        }
    }

    [GeneratedRegex("(?:from\\s+|import\\s*)['\\\"](\\.{1,2}/[^'\\\"]+\\.js)['\\\"]", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeImport();

    private sealed class NoopDiagnosticSink : IDiagnosticSink
    {
        internal static NoopDiagnosticSink Instance { get; } = new();

        public ValueTask WriteAsync(
            DiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed record AuditedProcessIdentity(
    int ProcessId,
    DateTimeOffset CreationTimeUtc,
    int ParentProcessId,
    DateTimeOffset? ParentCreationTimeUtc,
    string ImageName,
    DateTimeOffset StartObservedUtc,
    DateTimeOffset? ExitObservedUtc)
{
    internal bool ExitObserved => ExitObservedUtc is not null;
}

internal sealed class JobCompletionProcessAudit : IDisposable
{
    private const uint JobObjectAssociateCompletionPortInformation = 7;
    private const uint JobObjectMessageActiveProcessZero = 4;
    private const uint JobObjectMessageNewProcess = 6;
    private const uint JobObjectMessageExitProcess = 7;
    private const uint JobObjectMessageAbnormalExitProcess = 8;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorWaitTimeout = 258;
    private static readonly UIntPtr JobCompletionKey = new(0xEBA9u);
    private static readonly UIntPtr StopCompletionKey = new(0xEBAAu);

    private readonly SafeFileHandle _completionPort;
    private readonly SafeHandle _auditedJob;
    private readonly object _gate = new();
    private readonly Dictionary<AuditedProcessKey, AuditedProcessIdentity> _records = [];
    private readonly Dictionary<int, AuditedProcessKey> _activeByProcessId = [];
    private readonly TaskCompletionSource _activeProcessZero =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _failure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reader;
    private int _stopStarted;
    private int _disposed;

    private JobCompletionProcessAudit(
        SafeFileHandle completionPort,
        SafeHandle auditedJob)
    {
        _completionPort = completionPort;
        _auditedJob = auditedJob;
        _reader = Task.Run(ReadCompletionMessages);
    }

    internal static JobCompletionProcessAudit Create(WindowsJobObject job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var rawPort = NativeMethods.CreateIoCompletionPort(
            new IntPtr(-1),
            IntPtr.Zero,
            UIntPtr.Zero,
            numberOfConcurrentThreads: 1);
        if (rawPort == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var port = new SafeFileHandle(rawPort, ownsHandle: true);
        SafeHandle? jobHandle = null;
        try
        {
            jobHandle = job.DuplicateForTests();
            var association = new JobObjectAssociateCompletionPort
            {
                CompletionKey = new IntPtr(unchecked((long)JobCompletionKey.ToUInt64())),
                CompletionPort = port.DangerousGetHandle(),
            };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<JobObjectAssociateCompletionPort>());
            try
            {
                Marshal.StructureToPtr(association, buffer, fDeleteOld: false);
                if (!NativeMethods.SetInformationJobObject(
                        jobHandle.DangerousGetHandle(),
                        JobObjectAssociateCompletionPortInformation,
                        buffer,
                        checked((uint)Marshal.SizeOf<JobObjectAssociateCompletionPort>())))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            var audit = new JobCompletionProcessAudit(port, jobHandle);
            jobHandle = null;
            return audit;
        }
        catch
        {
            jobHandle?.Dispose();
            port.Dispose();
            throw;
        }
    }

    internal async Task WaitForActiveProcessZeroAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_activeProcessZero.Task, _failure.Task)
            .WaitAsync(timeout);
        if (ReferenceEquals(completed, _failure.Task))
        {
            throw await _failure.Task;
        }

        await _activeProcessZero.Task;
    }

    internal IReadOnlyList<AuditedProcessIdentity> Snapshot()
    {
        if (_failure.Task.IsCompletedSuccessfully)
        {
            throw _failure.Task.Result;
        }

        lock (_gate)
        {
            if (_activeByProcessId.Count != 0)
            {
                throw new InvalidOperationException("job-completion-audit-still-active");
            }

            return _records.Values
                .OrderBy(identity => identity.CreationTimeUtc)
                .ThenBy(identity => identity.ProcessId)
                .ToArray();
        }
    }

    internal async Task StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            await _reader.WaitAsync(timeout);
            return;
        }

        var failures = new List<Exception>();
        try
        {
            await WaitForActiveProcessZeroAsync(timeout);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        try
        {
            if (!NativeMethods.PostQueuedCompletionStatus(
                    _completionPort,
                    bytesTransferred: 0,
                    StopCompletionKey,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            await _reader.WaitAsync(timeout);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        if (failures.Count != 0)
        {
            throw new AggregateException("job-completion-audit-stop-failed", failures);
        }
    }

    internal void TerminateAuditedJob()
    {
        if (!NativeMethods.TerminateJobObject(_auditedJob, exitCode: 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _completionPort.Dispose();
            _auditedJob.Dispose();
        }
    }

    private void ReadCompletionMessages()
    {
        try
        {
            while (true)
            {
                if (!NativeMethods.GetQueuedCompletionStatus(
                        _completionPort,
                        out var message,
                        out var completionKey,
                        out var processValue,
                        uint.MaxValue))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorWaitTimeout)
                    {
                        continue;
                    }

                    throw new Win32Exception(error);
                }

                if (completionKey == StopCompletionKey)
                {
                    return;
                }

                if (completionKey != JobCompletionKey)
                {
                    throw new InvalidOperationException("job-completion-audit-key-mismatch");
                }

                var processId = checked((int)processValue.ToInt64());
                switch (message)
                {
                    case JobObjectMessageNewProcess:
                        RecordStart(Capture(processId));
                        break;
                    case JobObjectMessageExitProcess:
                    case JobObjectMessageAbnormalExitProcess:
                        RecordExit(processId);
                        break;
                    case JobObjectMessageActiveProcessZero:
                        lock (_gate)
                        {
                            if (_activeByProcessId.Count != 0)
                            {
                                throw new InvalidOperationException(
                                    "job-completion-audit-zero-with-active-process");
                            }
                        }
                        _activeProcessZero.TrySetResult();
                        break;
                }
            }
        }
        catch (Exception error)
        {
            _failure.TrySetResult(error);
            _activeProcessZero.TrySetException(error);
        }
    }

    private void RecordStart(AuditedProcessIdentity identity)
    {
        var key = new AuditedProcessKey(identity.ProcessId, identity.CreationTimeUtc);
        lock (_gate)
        {
            if (_activeByProcessId.ContainsKey(identity.ProcessId) ||
                !_records.TryAdd(key, identity) ||
                !_activeByProcessId.TryAdd(identity.ProcessId, key))
            {
                throw new InvalidOperationException("job-completion-audit-duplicate-identity");
            }
        }
    }

    private void RecordExit(int processId)
    {
        lock (_gate)
        {
            if (!_activeByProcessId.Remove(processId, out var key) ||
                !_records.TryGetValue(key, out var identity) ||
                identity.ExitObserved)
            {
                throw new InvalidOperationException("job-completion-audit-exit-without-start");
            }

            _records[key] = identity with { ExitObservedUtc = DateTimeOffset.UtcNow };
        }
    }

    private AuditedProcessIdentity Capture(int processId)
    {
        using var process = NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));
        if (process.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!NativeMethods.IsProcessInJob(process, _auditedJob, out var belongsToAuditedJob))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!belongsToAuditedJob)
        {
            throw new InvalidOperationException("job-completion-audit-membership-mismatch");
        }

        var creationTime = QueryCreationTime(process);
        var parentProcessId = QueryParentProcessId(process);
        DateTimeOffset? parentCreationTime = null;
        if (parentProcessId > 0)
        {
            using var parent = NativeMethods.OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                checked((uint)parentProcessId));
            if (parent.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            parentCreationTime = QueryCreationTime(parent);
        }

        var imagePath = new StringBuilder(32_768);
        var length = checked((uint)imagePath.Capacity);
        if (!NativeMethods.QueryFullProcessImageName(process, 0, imagePath, ref length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new AuditedProcessIdentity(
            processId,
            creationTime,
            parentProcessId,
            parentCreationTime,
            Path.GetFileName(imagePath.ToString()),
            DateTimeOffset.UtcNow,
            ExitObservedUtc: null);
    }

    private static DateTimeOffset QueryCreationTime(SafeProcessHandle process)
    {
        if (!NativeMethods.GetProcessTimes(
                process,
                out var creation,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new DateTimeOffset(
            DateTime.FromFileTimeUtc(creation.ToInt64()),
            TimeSpan.Zero);
    }

    private static int QueryParentProcessId(SafeProcessHandle process)
    {
        var information = new ProcessBasicInformation();
        var status = NativeMethods.NtQueryInformationProcess(
            process,
            processInformationClass: 0,
            ref information,
            checked((uint)Marshal.SizeOf<ProcessBasicInformation>()),
            out _);
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"job-completion-audit-parent-query-failed:{status:X8}");
        }

        return checked((int)information.InheritedFromUniqueProcessId.ToInt64());
    }

    private readonly record struct AuditedProcessKey(
        int ProcessId,
        DateTimeOffset CreationTimeUtc);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectAssociateCompletionPort
    {
        internal IntPtr CompletionKey;
        internal IntPtr CompletionPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        internal IntPtr Reserved1;
        internal IntPtr PebBaseAddress;
        internal IntPtr Reserved2_0;
        internal IntPtr Reserved2_1;
        internal IntPtr UniqueProcessId;
        internal IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;

        internal readonly long ToInt64() =>
            unchecked((long)(((ulong)HighDateTime << 32) | LowDateTime));
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateIoCompletionPort(
            IntPtr fileHandle,
            IntPtr existingCompletionPort,
            UIntPtr completionKey,
            uint numberOfConcurrentThreads);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            IntPtr job,
            uint informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetQueuedCompletionStatus(
            SafeFileHandle completionPort,
            out uint bytesTransferred,
            out UIntPtr completionKey,
            out IntPtr overlapped,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostQueuedCompletionStatus(
            SafeFileHandle completionPort,
            uint bytesTransferred,
            UIntPtr completionKey,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(
            SafeProcessHandle process,
            SafeHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(
            SafeHandle job,
            uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(
            SafeProcessHandle process,
            out NativeFileTime creationTime,
            out NativeFileTime exitTime,
            out NativeFileTime kernelTime,
            out NativeFileTime userTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(
            SafeProcessHandle process,
            uint flags,
            StringBuilder executableName,
            ref uint size);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryInformationProcess(
            SafeProcessHandle process,
            int processInformationClass,
            ref ProcessBasicInformation processInformation,
            uint processInformationLength,
            out uint returnLength);
    }
}

[CollectionDefinition("Published Node probe acceptance", DisableParallelization = true)]
public sealed class PublishedNodeProbeAcceptanceCollection;
