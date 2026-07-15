using HowardLab.EbayCrm.AppHost.Composition;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class PayloadPostExitBoundaryTests
{
    [Fact]
    public async Task Completion_RetainsLeaseUntilNativeExitThenVerifiesExactlyOnceAndReleases()
    {
        var nativeExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new TrackingLease();
        var verificationCount = 0;
        var verifier = new OneShotPayloadClosureVerifier(() => verificationCount++);

        var completion = PayloadPostExitBoundary.Start(
            nativeExit.Task,
            verifier,
            lease);

        Assert.False(completion.IsCompleted);
        Assert.False(lease.IsDisposed);
        Assert.Equal(0, verificationCount);

        nativeExit.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        await completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(lease.IsDisposed);
        Assert.Equal(1, verificationCount);
    }

    [Fact]
    public async Task Completion_VerifierFailureStillReleasesAfterNativeExit()
    {
        var nativeExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new TrackingLease();
        var verifier = new OneShotPayloadClosureVerifier(
            () => throw new InvalidOperationException("sensitive verifier failure"));
        var completion = PayloadPostExitBoundary.Start(nativeExit.Task, verifier, lease);

        nativeExit.SetResult();
        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() => completion);

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public async Task Completion_LateVerifierFailureReportsSanitizedReasonExactlyOnce()
    {
        const string secret = "late-boundary-secret";
        var nativeExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new TrackingLease();
        var verifier = new OneShotPayloadClosureVerifier(
            () => throw new InvalidOperationException(secret));
        var reported = new List<string>();
        var completion = PayloadPostExitBoundary.Start(
            nativeExit.Task,
            verifier,
            lease,
            reasonCode =>
            {
                reported.Add(reasonCode);
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAsync<TimeoutException>(() =>
            completion.WaitAsync(TimeSpan.FromMilliseconds(100)));
        nativeExit.SetResult();

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() => completion);
        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.Equal(["role-payload-trust-failed"], reported);
        Assert.DoesNotContain(secret, string.Join('|', reported), StringComparison.Ordinal);
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public async Task Completion_TerminalFaultConsumerRunsOnceAfterReporterCompletes()
    {
        var nativeExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reporterEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReporter = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faultConsumed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reporterCount = 0;
        var consumerCount = 0;
        var completion = PayloadPostExitBoundary.Start(
            nativeExit.Task,
            new OneShotPayloadClosureVerifier(
                () => throw new InvalidOperationException("terminal-fault-secret")),
            new TrackingLease(),
            async _ =>
            {
                Interlocked.Increment(ref reporterCount);
                reporterEntered.TrySetResult();
                await releaseReporter.Task;
            },
            () =>
            {
                Interlocked.Increment(ref consumerCount);
                faultConsumed.TrySetResult();
            });

        nativeExit.SetResult();
        await reporterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref reporterCount));
        Assert.Equal(0, Volatile.Read(ref consumerCount));
        Assert.False(completion.IsCompleted);

        releaseReporter.SetResult();
        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() => completion);
        await faultConsumed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.Equal(1, Volatile.Read(ref reporterCount));
        Assert.Equal(1, Volatile.Read(ref consumerCount));
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task Completion_FaultedOrCanceledNativeObservationNeverReleasesWithoutExitProof(
        string outcome)
    {
        var nativeExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new TrackingLease();
        var verifier = new OneShotPayloadClosureVerifier(() =>
            throw new InvalidOperationException("must not verify"));
        var completion = PayloadPostExitBoundary.Start(nativeExit.Task, verifier, lease);

        if (outcome == "fault")
        {
            nativeExit.SetException(new InvalidOperationException("simulated observation fault"));
        }
        else
        {
            nativeExit.SetCanceled();
        }

        await Assert.ThrowsAsync<TimeoutException>(() =>
            completion.WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.False(lease.IsDisposed);
    }

    private sealed class TrackingLease : IDisposable
    {
        private int _disposed;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
