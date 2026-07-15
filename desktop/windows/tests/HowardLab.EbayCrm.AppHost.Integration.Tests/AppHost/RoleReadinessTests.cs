using System.Net;
using System.Text;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class RoleReadinessTests
{
    [Fact]
    public async Task NotReadyThenReadyPollsWithoutOverlappingRequests()
    {
        var handler = new SequencedHealthHandler("not-ready", "ready");
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(),
            () => handler);
        var generation = await StartWorkerAsync(harness);

        var result = await harness.Executor.ExecuteAsync(WaitWorker(generation));

        Assert.Equal(generation, Assert.IsType<RoleReady>(result).Value);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, handler.MaxConcurrentCalls);
    }

    [Fact]
    public async Task NotReadyBeyondReadinessDeadlineReturnsOperationTimedOut()
    {
        var handler = new SequencedHealthHandler("not-ready");
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(readiness: TimeSpan.FromMilliseconds(80)),
            () => handler);
        var generation = await StartWorkerAsync(harness);

        var result = await harness.Executor.ExecuteAsync(WaitWorker(generation));

        var timedOut = Assert.IsType<OperationTimedOut>(result);
        Assert.Equal(generation, timedOut.Value);
        Assert.Equal(generation.OperationId, timedOut.OperationId);
        Assert.True(handler.CallCount >= 2);
        Assert.Equal(1, harness.Executor.ReadinessTimeoutDiagnosticCountForTests);
    }

    [Fact]
    public async Task ReconciliationPollsReadinessAndReturnsRunningOnlyAfterReady()
    {
        var initial = new SequencedHealthHandler("not-ready");
        var reconciliation = new SequencedHealthHandler("not-ready", "ready");
        var handlers = new Queue<HttpMessageHandler>([initial, reconciliation]);
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(readiness: TimeSpan.FromMilliseconds(60)),
            () => handlers.Dequeue());
        var generation = await StartWorkerAsync(harness);
        Assert.IsType<OperationTimedOut>(
            await harness.Executor.ExecuteAsync(WaitWorker(generation)));

        var result = await harness.Executor.ExecuteAsync(ReconcileWorker(generation));

        Assert.Equal(ReconciledState.Running, Assert.IsType<Reconciled>(result).State);
        Assert.Equal(2, reconciliation.CallCount);
        Assert.Equal(1, reconciliation.MaxConcurrentCalls);

        var finalWait = await harness.Executor.ExecuteAsync(WaitWorker(generation));
        Assert.Equal(generation, Assert.IsType<RoleReady>(finalWait).Value);
        Assert.Equal(2, reconciliation.CallCount);
    }

    [Fact]
    public async Task ConfirmedReadinessChecksRawDisconnectBeforeDelayedMonitorWrapper()
    {
        var initial = new SequencedHealthHandler("not-ready");
        var reconciliation = new SequencedHealthHandler("ready");
        var handlers = new Queue<HttpMessageHandler>([initial, reconciliation]);
        var monitorRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(readiness: TimeSpan.FromMilliseconds(50)),
            () => handlers.Dequeue());
        harness.Executor.ControlMonitorDisconnectObservedForTests = _ => monitorRelease.Task;
        var generation = await StartWorkerAsync(harness);
        Assert.IsType<OperationTimedOut>(
            await harness.Executor.ExecuteAsync(WaitWorker(generation)));
        Assert.Equal(
            ReconciledState.Running,
            Assert.IsType<Reconciled>(
                await harness.Executor.ExecuteAsync(ReconcileWorker(generation))).State);

        await harness.Executor.DisconnectRoleControlForTestsAsync(RuntimeRole.Worker);
        await harness.Executor.WaitForControlDisconnectForTestsAsync(RuntimeRole.Worker)
            .WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
                harness.Executor.ExecuteAsync(WaitWorker(generation)));
            Assert.Equal("role-control-disconnected-before-ready", error.ReasonCode);
        }
        finally
        {
            monitorRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task ReconciliationExhaustionReturnsOperationTimedOut()
    {
        var initial = new SequencedHealthHandler("not-ready");
        var reconciliation = new SequencedHealthHandler("not-ready");
        var handlers = new Queue<HttpMessageHandler>([initial, reconciliation]);
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(
                readiness: TimeSpan.FromMilliseconds(50),
                reconciliation: TimeSpan.FromMilliseconds(80)),
            () => handlers.Dequeue());
        var generation = await StartWorkerAsync(harness);
        Assert.IsType<OperationTimedOut>(
            await harness.Executor.ExecuteAsync(WaitWorker(generation)));

        var result = await harness.Executor.ExecuteAsync(ReconcileWorker(generation));

        Assert.IsType<OperationTimedOut>(result);
        Assert.True(reconciliation.CallCount >= 2);
        Assert.Equal(1, harness.Executor.ReadinessTimeoutDiagnosticCountForTests);
    }

    [Theory]
    [InlineData(HealthResponseKind.Malformed, "role-health-malformed")]
    [InlineData(HealthResponseKind.IdentityMismatch, "role-health-identity-mismatch")]
    [InlineData(HealthResponseKind.NonSuccess, "role-health-http-status-invalid")]
    [InlineData(HealthResponseKind.UnknownStatus, "role-health-status-invalid")]
    [InlineData(HealthResponseKind.Empty, "role-health-empty")]
    [InlineData(HealthResponseKind.NegativeActiveWork, "role-health-identity-mismatch")]
    [InlineData(HealthResponseKind.Oversized, "role-health-malformed")]
    [InlineData(HealthResponseKind.DuplicateProperty, "role-health-malformed")]
    [InlineData(HealthResponseKind.ExtraProperty, "role-health-malformed")]
    public async Task InvalidHealthResponseFailsImmediately(
        HealthResponseKind kind,
        string expectedReason)
    {
        var handler = new SequencedHealthHandler(kind);
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(),
            () => handler);
        var generation = await StartWorkerAsync(harness);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(WaitWorker(generation)));

        Assert.Equal(expectedReason, error.ReasonCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("control-disconnect", "role-control-disconnected-before-ready")]
    public async Task RetainedProcessOrControlLossStopsReadinessImmediately(
        string fixtureMode,
        string expectedReason)
    {
        var handler = new SequencedHealthHandler("not-ready");
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(),
            () => handler);
        harness.FixtureRoleLaunchPlanProvider.WorkerModeForTests = fixtureMode;
        var generation = await StartWorkerAsync(harness);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(WaitWorker(generation)));

        Assert.Equal(expectedReason, error.ReasonCode);
    }

    [Fact]
    public async Task ReadyResponseDoesNotWinOverAnAlreadyExitedRetainedProcess()
    {
        var boundary = new BlockingRoleOperationBoundary(
            RuntimeRole.Worker,
            RoleOperationBoundaryPoint.StopAccepted);
        var handler = new SequencedHealthHandler("ready");
        await using var harness = RoleExecutorHarness.Create(
            boundary,
            Deadlines(),
            () => handler);
        var generation = await StartWorkerAsync(harness);
        handler.BeforeResponse = async () =>
        {
            boundary.Terminate(generation);
            await boundary.WaitUntilExitedAsync(generation, TimeSpan.FromSeconds(5));
        };

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(WaitWorker(generation)));

        Assert.Contains(
            error.ReasonCode,
            new[]
            {
                "role-process-exited-before-ready",
                "role-control-disconnected-before-ready",
            });
    }

    [Fact]
    public async Task DisconnectAfterReadyValidationCannotCrossTheMonitorHandoff()
    {
        var handler = new SequencedHealthHandler("ready");
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(),
            () => handler);
        var generation = await StartWorkerAsync(harness);
        harness.Executor.ReadinessValidatedForTests = async role =>
        {
            await harness.Executor.DisconnectRoleControlForTestsAsync(role);
            await harness.Executor.WaitForControlDisconnectForTestsAsync(role)
                .WaitAsync(TimeSpan.FromSeconds(2));
        };

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(WaitWorker(generation)));

        Assert.Equal("role-control-disconnected-before-ready", error.ReasonCode);
    }

    [Fact]
    public async Task ProcessExitCancelsAndObservesAStalledHealthBody()
    {
        var boundary = new BlockingRoleOperationBoundary(
            RuntimeRole.Worker,
            RoleOperationBoundaryPoint.StopAccepted);
        var handler = new StalledBodyHandler();
        await using var harness = RoleExecutorHarness.Create(
            boundary,
            Deadlines(),
            () => handler);
        var generation = await StartWorkerAsync(harness);
        var readiness = harness.Executor.ExecuteAsync(WaitWorker(generation));
        await handler.BodyReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        boundary.Terminate(generation);
        await boundary.WaitUntilExitedAsync(generation, TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(async () =>
            await readiness.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains(
            error.ReasonCode,
            new[]
            {
                "role-process-exited-before-ready",
                "role-control-disconnected-before-ready",
            });
        Assert.True(handler.BodyReadCanceled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ControlDisconnectCancelsAndObservesAStalledHealthBody()
    {
        var handler = new StalledBodyHandler();
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(),
            () => handler);
        var generation = await StartWorkerAsync(harness);
        var readiness = harness.Executor.ExecuteAsync(WaitWorker(generation));
        await handler.BodyReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await harness.Executor.DisconnectRoleControlForTestsAsync(RuntimeRole.Worker);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(async () =>
            await readiness.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("role-control-disconnected-before-ready", error.ReasonCode);
        Assert.True(handler.BodyReadCanceled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CallerCancellationWinsAfterStalledBodyLoserIsObserved()
    {
        var handler = new StalledBodyHandler();
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(),
            () => handler);
        var generation = await StartWorkerAsync(harness);
        using var cancellation = new CancellationTokenSource();
        harness.Executor.ReadinessLoserObservedForTests = cancellation.Cancel;
        var readiness = harness.Executor.ExecuteAsync(
            WaitWorker(generation),
            cancellation.Token);
        await handler.BodyReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await harness.Executor.DisconnectRoleControlForTestsAsync(RuntimeRole.Worker);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await readiness.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(handler.BodyReadCanceled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CallerCancellationStopsPolling()
    {
        var handler = new SequencedHealthHandler("not-ready");
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            Deadlines(readiness: TimeSpan.FromSeconds(5)),
            () => handler);
        var generation = await StartWorkerAsync(harness);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Executor.ExecuteAsync(WaitWorker(generation), cancellation.Token));

        var callsAfterCancellation = handler.CallCount;
        await Task.Delay(50);
        Assert.Equal(callsAfterCancellation, handler.CallCount);
        Assert.Equal(1, handler.MaxConcurrentCalls);
    }

    [Fact]
    public async Task CallerCancellationDuringLateAcceptIsNotAnInternalReconciliationTimeout()
    {
        await using var harness = RoleExecutorHarness.Create(
            NoopRoleOperationBoundary.Instance,
            new RoleOperationDeadlines(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(20)));
        harness.FixtureRoleLaunchPlanProvider.WorkerModeForTests = "pipe-timeout";
        var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
        Assert.IsType<OperationTimedOut>(await harness.Executor.ExecuteAsync(new LifecycleCommand(
            LifecycleCommandType.StartWorker,
            generation,
            generation.OperationId,
            LifecycleDeadlineKey.WorkerStart)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Executor.ExecuteAsync(ReconcileWorker(generation), cancellation.Token));
    }

    [Fact]
    public void ProductionHealthHandlerDisablesProxyAndRedirects()
    {
        using var handler = LifecycleCommandExecutor.CreateRoleHealthMessageHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    private static RoleOperationDeadlines Deadlines(
        TimeSpan? readiness = null,
        TimeSpan? reconciliation = null) => new(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            reconciliation ?? TimeSpan.FromSeconds(2),
            readiness ?? TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20));

    private static async Task<ProcessGeneration> StartWorkerAsync(RoleExecutorHarness harness)
    {
        var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
        var started = await harness.Executor.ExecuteAsync(new LifecycleCommand(
            LifecycleCommandType.StartWorker,
            generation,
            generation.OperationId,
            LifecycleDeadlineKey.WorkerStart));
        Assert.Equal(generation, Assert.IsType<RoleStarted>(started).Value);
        return generation;
    }

    private static LifecycleCommand WaitWorker(ProcessGeneration generation) => new(
        LifecycleCommandType.WaitForWorker,
        generation,
        generation.OperationId,
        LifecycleDeadlineKey.WorkerReadiness);

    private static LifecycleCommand ReconcileWorker(ProcessGeneration generation) => new(
        LifecycleCommandType.ReconcileRoleStart,
        generation,
        generation.OperationId,
        LifecycleDeadlineKey.RoleReconciliation);

    public enum HealthResponseKind
    {
        Malformed,
        IdentityMismatch,
        NonSuccess,
        UnknownStatus,
        Empty,
        NegativeActiveWork,
        Oversized,
        DuplicateProperty,
        ExtraProperty,
    }

    private sealed class SequencedHealthHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Queue<object> _responses;
        private int _activeCalls;
        private int _callCount;
        private int _maxConcurrentCalls;

        internal SequencedHealthHandler(params string[] statuses)
            : this(statuses.Cast<object>().ToArray())
        {
        }

        internal SequencedHealthHandler(params HealthResponseKind[] kinds)
            : this(kinds.Cast<object>().ToArray())
        {
        }

        private SequencedHealthHandler(params object[] responses) =>
            _responses = new Queue<object>(responses);

        internal int CallCount => Volatile.Read(ref _callCount);

        internal int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        internal Func<Task>? BeforeResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(5, cancellationToken);
                if (BeforeResponse is { } beforeResponse)
                {
                    await beforeResponse();
                }
                object response;
                lock (_gate)
                {
                    response = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
                }

                return CreateResponse(request, response);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private static HttpResponseMessage CreateResponse(HttpRequestMessage request, object response)
        {
            if (response is HealthResponseKind.NonSuccess)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (response is HealthResponseKind.Empty)
            {
                return Json(HttpStatusCode.OK, string.Empty);
            }

            if (response is HealthResponseKind.Malformed)
            {
                return Json(HttpStatusCode.OK, "{");
            }

            if (response is HealthResponseKind.Oversized)
            {
                return Json(HttpStatusCode.OK, new string('x', 16 * 1024));
            }

            var status = response switch
            {
                HealthResponseKind.UnknownStatus => "future",
                string value => value,
                _ => "ready",
            };
            var generation = long.Parse(request.Headers.GetValues("X-AppHost-Generation").Single());
            var build = request.Headers.GetValues("X-AppHost-Build").Single();
            var nonce = request.Headers.GetValues("X-AppHost-Nonce").Single();
            if (response is HealthResponseKind.IdentityMismatch)
            {
                nonce += "-wrong";
            }

            var activeWorkRemaining = response is HealthResponseKind.NegativeActiveWork ? -1 : 0;
            var payload = JsonSerializer.Serialize(
                new HealthPayload(
                    ControlProtocolConstants.CurrentVersion,
                    build,
                    generation,
                    nonce,
                    status,
                    activeWorkRemaining),
                ControlFrameCodec.SerializerOptions);
            if (response is HealthResponseKind.DuplicateProperty)
            {
                payload = payload.Replace(
                    "\"status\":\"ready\"",
                    "\"status\":\"ready\",\"status\":\"ready\"",
                    StringComparison.Ordinal);
            }
            else if (response is HealthResponseKind.ExtraProperty)
            {
                payload = payload.Insert(payload.Length - 1, ",\"extra\":true");
            }
            return Json(HttpStatusCode.OK, payload);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string payload) => new(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxConcurrentCalls);
                if (candidate <= current ||
                    Interlocked.CompareExchange(ref _maxConcurrentCalls, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        internal TaskCompletionSource BodyReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource BodyReadCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StalledBodyContent(BodyReadStarted, BodyReadCanceled),
            });
    }

    private sealed class StalledBodyContent(
        TaskCompletionSource readStarted,
        TaskCompletionSource readCanceled) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StalledReadStream(readStarted, readCanceled));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new StalledReadStream(readStarted, readCanceled));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StalledReadStream(
        TaskCompletionSource readStarted,
        TaskCompletionSource readCanceled) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                readCanceled.TrySetResult();
                throw;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
