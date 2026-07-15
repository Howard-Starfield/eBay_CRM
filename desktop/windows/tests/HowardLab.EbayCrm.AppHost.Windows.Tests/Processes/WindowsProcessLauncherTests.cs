using System.Text.Json;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Processes;

public sealed class WindowsProcessLauncherTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LaunchAsync_RoundTripsArgumentsAndUsesOnlyExplicitEnvironment()
    {
        const string secret = "task-5-secret-canary";
        var arguments = new[] { "echo-launch", "", "two words", "a\"b", "trailing slash \\", "雪だるま" };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TASK5_VISIBLE"] = "visible-value",
        };
        var secrets = new Dictionary<string, SecretValue>(StringComparer.Ordinal)
        {
            ["TASK5_SECRET"] = new(secret),
        };

        using var job = WindowsJobObject.CreateKillOnClose();
        await using var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(arguments, environment, secrets),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);

        Assert.Equal(0, await process.Completion.WaitAsync(Deadline));
        var echo = JsonSerializer.Deserialize<EchoResult>(process.StandardOutput.Snapshot());
        Assert.NotNull(echo);
        Assert.Equal(arguments[1..], echo.Arguments);
        Assert.Equal(["TASK5_SECRET", "TASK5_VISIBLE"], echo.EnvironmentNames);
        Assert.DoesNotContain("PATH", echo.EnvironmentNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, process.StandardOutput.Snapshot(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, process.StandardError.Snapshot(), StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(FixturePath), process.Identity.VerifiedImagePath, StringComparer.OrdinalIgnoreCase);
        Assert.True(job.Contains(process.ProcessHandle));
    }

    [Fact]
    public async Task LaunchAsync_DrainsStdoutAndStderrIndependently()
    {
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["output"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);

        Assert.Equal(0, await process.Completion.WaitAsync(Deadline));
        Assert.Equal("stdout-line\n", process.StandardOutput.Snapshot());
        Assert.Equal("stderr-line\n", process.StandardError.Snapshot());
    }

    [Fact]
    public async Task LaunchAsync_WritesBoundedStandardInputAndClosesPipe()
    {
        var input = Encoding.UTF8.GetBytes("transactional migration over stdin");
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["stdin-echo"]) with { StandardInput = input },
            job,
            CancellationToken.None);

        Assert.Equal(0, await launched.Completion.WaitAsync(Deadline));
        Assert.Equal("transactional migration over stdin\n", launched.StandardOutput.Snapshot());
    }

    [Fact]
    public async Task Completion_ToleratesChildExitBeforeLargeStandardInputIsConsumed()
    {
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["exit-without-stdin"]) with
            {
                StandardInput = new byte[WindowsProcessLauncher.MaximumStandardInputBytes],
            },
            job,
            CancellationToken.None);

        Assert.Equal(0, await launched.Completion.WaitAsync(Deadline));
    }

    [Fact]
    public async Task LaunchAsync_DrainsOutputWhileWritingStandardInputWithoutDeadlock()
    {
        var input = Enumerable.Repeat((byte)'a', 256 * 1024).ToArray();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["output-before-stdin"]) with { StandardInput = input },
            job,
            CancellationToken.None);

        Assert.Equal(0, await launched.Completion.WaitAsync(Deadline));
        Assert.EndsWith($"\n{input.Length}\n", launched.StandardOutput.Snapshot(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FloodOutputMode_DrainsBoundedOutputWithoutDeadlock()
    {
        var input = Enumerable.Repeat((byte)'a', 256 * 1024).ToArray();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["flood-output"]) with { StandardInput = input },
            job,
            CancellationToken.None);

        Assert.Equal(0, await launched.Completion.WaitAsync(Deadline));
        Assert.EndsWith($"\n{input.Length}\n", launched.StandardOutput.Snapshot(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_CancelsChildThatNeverReadsStandardInputWithoutDeadlock()
    {
        using var job = WindowsJobObject.CreateKillOnClose();
        var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["hold"]) with
            {
                StandardInput = new byte[WindowsProcessLauncher.MaximumStandardInputBytes],
            },
            job,
            CancellationToken.None);

        await launched.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [Fact]
    public async Task ForceCloseAfterJobClose_SynchronouslyReleasesProcessAndPipeResources()
    {
        using var job = WindowsJobObject.CreateKillOnClose();
        var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["hold"]) with
            {
                StandardInput = new byte[WindowsProcessLauncher.MaximumStandardInputBytes],
            },
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);

        job.Dispose();
        process.ForceCloseAfterJobClose();

        Assert.True(process.ProcessHandle.IsClosed);
        await process.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task ForceCloseAfterJobClose_IsIdempotent()
    {
        using var job = WindowsJobObject.CreateKillOnClose();
        var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["hold"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);

        job.Dispose();
        process.ForceCloseAfterJobClose();
        process.ForceCloseAfterJobClose();

        Assert.True(process.ProcessHandle.IsClosed);
        await process.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task NativeExitObservation_RemainsPendingUntilRunningChildActuallyExits()
    {
        using var job = WindowsJobObject.CreateKillOnClose();
        var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["hold"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);
        using var child = System.Diagnostics.Process.GetProcessById(process.Identity.ProcessId);

        Assert.False(process.NativeExitObservation.IsCompleted);

        child.Kill();
        await process.NativeExitObservation.WaitAsync(Deadline);

        Assert.True(child.WaitForExit(checked((int)Deadline.TotalMilliseconds)));
        await process.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [Fact]
    public async Task NativeExitObservation_SurvivesForceCloseAndSignalsOnlyAfterLaterExit()
    {
        using var job = WindowsJobObject.CreateKillOnClose();
        var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["hold"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);
        using var child = System.Diagnostics.Process.GetProcessById(process.Identity.ProcessId);

        process.ForceCloseAfterJobClose();

        Assert.True(process.ProcessHandle.IsClosed);
        Assert.False(child.HasExited);
        Assert.False(process.NativeExitObservation.IsCompleted);

        child.Kill();
        await process.NativeExitObservation.WaitAsync(Deadline);

        Assert.True(child.WaitForExit(checked((int)Deadline.TotalMilliseconds)));
        await process.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task NativeExitObservation_CompletesAndReleasesProcessAfterNormalDispose()
    {
        using var job = WindowsJobObject.CreateKillOnClose();
        var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(["hold"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);
        using var child = System.Diagnostics.Process.GetProcessById(process.Identity.ProcessId);

        await process.DisposeAsync().AsTask().WaitAsync(Deadline);
        await process.NativeExitObservation.WaitAsync(Deadline);

        Assert.True(process.ProcessHandle.IsClosed);
        Assert.True(child.WaitForExit(checked((int)Deadline.TotalMilliseconds)));
    }

    [Fact]
    public async Task LaunchAsync_CleanupIndeterminateCarriesAuthoritativeNativeExitObservation()
    {
        var identityVerifier = new ThrowingIdentityVerifier();
        var cleanup = new IndeterminateCleanupPolicy();
        using var job = WindowsJobObject.CreateKillOnClose();
        var launcher = new WindowsProcessLauncher(
            NoopDiagnosticSink.Instance,
            maxOutputBytes: 64 * 1024,
            maxLineBytes: 4 * 1024,
            cleanup,
            identityVerifier);

        var error = await Assert.ThrowsAsync<ProcessCleanupException>(async () =>
            await launcher.LaunchAsync(
                CreateSpecification(["hold"]),
                job,
                CancellationToken.None));

        Assert.NotNull(error.AuthoritativeNativeExitObservation);
        Assert.False(error.AuthoritativeNativeExitObservation.IsCompleted);
        using var child = System.Diagnostics.Process.GetProcessById(identityVerifier.ProcessId);

        child.Kill();
        await error.AuthoritativeNativeExitObservation.WaitAsync(Deadline);

        Assert.True(child.WaitForExit(checked((int)Deadline.TotalMilliseconds)));
    }

    [Fact]
    public async Task LaunchAsync_ExitObservationConstructionFailureRetainsFailSafeWhenCleanupIsIndeterminate()
    {
        var identityVerifier = new ThrowingIdentityVerifier();
        var cleanup = new IndeterminateCleanupPolicy();
        var observations = new ThrowingNativeExitObservationFactory();
        using var job = WindowsJobObject.CreateKillOnClose();
        var launcher = new WindowsProcessLauncher(
            NoopDiagnosticSink.Instance,
            maxOutputBytes: 64 * 1024,
            maxLineBytes: 4 * 1024,
            cleanup,
            identityVerifier,
            observations);

        var error = await Assert.ThrowsAsync<ProcessCleanupException>(async () =>
            await launcher.LaunchAsync(
                CreateSpecification(["hold"]),
                job,
                CancellationToken.None));

        Assert.Equal(2, observations.CreateCount);
        Assert.Equal(
            NativeExitObservationKind.ProofUnavailableContainment,
            error.NativeExitObservationKind);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            error.PayloadLifetimeBoundaryObservation.WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.True(job.TerminateTree(exitCode: 1).Succeeded);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            error.PayloadLifetimeBoundaryObservation.WaitAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task LaunchAsync_RedactsSecretEnvironmentFromBothOutputStreams()
    {
        const string secret = "task-5-stream-secret";
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var launched = await CreateLauncher().LaunchAsync(
            CreateSpecification(
                ["output-secret"],
                secrets: new Dictionary<string, SecretValue>
                {
                    ["TASK5_SECRET"] = new(secret),
                }),
            job,
            CancellationToken.None);

        Assert.Equal(0, await launched.Completion.WaitAsync(Deadline));
        Assert.Equal("[REDACTED]\n", launched.StandardOutput.Snapshot());
        Assert.Equal("[REDACTED]\n", launched.StandardError.Snapshot());
        Assert.DoesNotContain(secret, launched.StandardOutput.Snapshot(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, launched.StandardError.Snapshot(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_DoesNotWaitIndefinitelyForDescendantOutputHandles()
    {
        var diagnostics = new CapturingDiagnosticSink();
        using var job = WindowsJobObject.CreateKillOnClose();
        var specification = CreateSpecification(["orphan-output-handles"]) with
        {
            OutputDrainTimeout = TimeSpan.FromMilliseconds(100),
        };
        await using var launched = await CreateLauncher(diagnostics).LaunchAsync(
            specification,
            job,
            CancellationToken.None);

        Assert.Equal(0, await launched.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Throws<InvalidOperationException>(() => launched.StandardOutput.Append("late output"));
        Assert.Throws<InvalidOperationException>(() => launched.StandardError.Append("late error"));
        Assert.Equal(
            "process.output_drain_timeout",
            await diagnostics.EventName.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task LaunchAsync_ReportsNativeFailureUsingOnlyRoleAndErrorCode()
    {
        const string secret = "native-failure-secret";
        var invalidExecutable = Path.GetTempFileName();
        try
        {
            var specification = CreateSpecification(
                ["ignored"],
                secrets: new Dictionary<string, SecretValue>
                {
                    ["TASK5_SECRET"] = new(secret),
                }) with
            {
                ApplicationPath = Path.GetFullPath(invalidExecutable),
            };
            using var job = WindowsJobObject.CreateKillOnClose();

            var error = await Assert.ThrowsAsync<ProcessLaunchException>(async () =>
                await CreateLauncher().LaunchAsync(
                    specification,
                    job,
                    CancellationToken.None));

            Assert.Equal(RuntimeRole.Server, error.Role);
            Assert.NotEqual(0, error.Win32ErrorCode);
            Assert.Equal(
                $"Could not launch Server; Win32 error {error.Win32ErrorCode}.",
                error.Message);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(invalidExecutable, error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(invalidExecutable);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidSpecifications))]
    public async Task LaunchAsync_RejectsInvalidInputsBeforeLaunch(LaunchSpecification specification)
    {
        using var job = WindowsJobObject.CreateKillOnClose();

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await CreateLauncher().LaunchAsync(
                specification,
                job,
                CancellationToken.None));
    }

    public static TheoryData<LaunchSpecification> InvalidSpecifications()
    {
        var valid = CreateSpecification(["echo-launch"]);
        return new TheoryData<LaunchSpecification>
        {
            valid with { ApplicationPath = "relative.exe" },
            valid with { ApplicationPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe") },
            valid with { WorkingDirectory = "." },
            valid with { WorkingDirectory = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}") },
            valid with { Arguments = ["before\0after"] },
            valid with { Environment = new Dictionary<string, string> { [""] = "value" } },
            valid with { Environment = new Dictionary<string, string> { ["BAD=KEY"] = "value" } },
            valid with { Environment = new Dictionary<string, string> { ["BAD\0KEY"] = "value" } },
            valid with { Environment = new Dictionary<string, string> { ["GOOD"] = "before\0after" } },
            valid with
            {
                Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["Path"] = "visible" },
                SecretEnvironment = new Dictionary<string, SecretValue>(StringComparer.Ordinal) { ["PATH"] = new("secret") },
            },
            valid with { OutputDrainTimeout = TimeSpan.Zero },
            valid with { StandardInput = new byte[WindowsProcessLauncher.MaximumStandardInputBytes + 1] },
        };
    }

    internal static WindowsProcessLauncher CreateLauncher(IDiagnosticSink? diagnosticSink = null) =>
        new(
            diagnosticSink ?? NoopDiagnosticSink.Instance,
            maxOutputBytes: 64 * 1024,
            maxLineBytes: 4 * 1024);

    internal static LaunchSpecification CreateSpecification(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyDictionary<string, SecretValue>? secrets = null) =>
        new(
            RuntimeRole.Server,
            new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid()),
            FixturePath,
            arguments,
            AppContext.BaseDirectory,
            environment ?? new Dictionary<string, string>(),
            secrets ?? new Dictionary<string, SecretValue>(),
            TimeSpan.FromSeconds(2));

    internal static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "HowardLab.EbayCrm.AppHost.Fixture.exe");

    private sealed record EchoResult(string[] Arguments, string[] EnvironmentNames);

    private sealed class NoopDiagnosticSink : IDiagnosticSink
    {
        internal static readonly NoopDiagnosticSink Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            DiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class CapturingDiagnosticSink : IDiagnosticSink
    {
        internal TaskCompletionSource<string> EventName { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            DiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            EventName.TrySetResult(diagnosticEvent.Name);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingIdentityVerifier : IWindowsProcessIdentityVerifier
    {
        internal int ProcessId { get; private set; }

        public SupervisedProcessIdentity Capture(
            RuntimeRole role,
            ProcessGeneration generation,
            SafeProcessHandle processHandle)
        {
            ProcessId = checked((int)NativeMethods.GetProcessId(processHandle));
            throw new Win32Exception(87);
        }
    }

    private sealed class IndeterminateCleanupPolicy : IProcessCleanupPolicy
    {
        public ProcessCleanupResult Cleanup(
            SafeProcessHandle processHandle,
            IProcessTreeTerminator job) =>
            new(
                Signaled: false,
                EscalatedToJob: true,
                ProcessTerminationErrorCode: null,
                JobTerminationErrorCode: null,
                WaitErrorCode: null,
                TimedOut: true);
    }

    private sealed class ThrowingNativeExitObservationFactory
        : IWindowsNativeExitObservationFactory
    {
        private int _createCount;

        internal int CreateCount => Volatile.Read(ref _createCount);

        public Task Create(SafeProcessHandle processHandle)
        {
            Interlocked.Increment(ref _createCount);
            throw new Win32Exception(8);
        }
    }
}
