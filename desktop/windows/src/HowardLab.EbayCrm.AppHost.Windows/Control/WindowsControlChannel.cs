using System.IO.Pipes;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Control;

public sealed class WindowsControlChannel : IControlChannel
{
    private const double MaxCancelAfterMilliseconds = uint.MaxValue - 1d;
    public const string PipeEnvironmentVariable = "HOWARDLAB_APPHOST_CONTROL_PIPE";
    public const string NonceEnvironmentVariable = "HOWARDLAB_APPHOST_CONTROL_NONCE";
    public const string RoleEnvironmentVariable = "HOWARDLAB_APPHOST_CONTROL_ROLE";
    public const string GenerationEnvironmentVariable = "HOWARDLAB_APPHOST_CONTROL_GENERATION";
    public const string OperationEnvironmentVariable = "HOWARDLAB_APPHOST_CONTROL_OPERATION";
    public const string BuildEnvironmentVariable = "HOWARDLAB_APPHOST_CONTROL_BUILD";

    private readonly NamedPipeServerStream _pipe;
    private readonly TimeSpan _operationTimeout;
    private readonly ControlFrameCodec _readCodec = new();
    private readonly ControlFrameCodec _writeCodec = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _validatorGate = new();
    private readonly object _disposeGate = new();
    private ControlSessionValidator? _validator;
    private SafeProcessHandle? _clientProcess;
    private Task? _disposeTask;
    private int _frameCount;
    private int _state;
    private int _resourcesClosed;

    private WindowsControlChannel(
        ControlEndpointIdentity endpointIdentity,
        NamedPipeServerStream pipe,
        TimeSpan operationTimeout)
    {
        EndpointIdentity = endpointIdentity;
        _pipe = pipe;
        _operationTimeout = operationTimeout;
    }

    public ControlEndpointIdentity EndpointIdentity { get; }

    internal Func<CancellationToken, Task>? AuthenticationPublishHook { get; set; }

    internal bool ResourcesClosedForTests => Volatile.Read(ref _resourcesClosed) != 0;

    public static WindowsControlChannel CreateBeforeLaunch(
        RuntimeRole role,
        long generation,
        Guid startupOperationId,
        string expectedBuildIdentity,
        TimeSpan operationTimeout)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (startupOperationId == Guid.Empty)
        {
            throw new ArgumentException("The startup operation ID cannot be empty.", nameof(startupOperationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBuildIdentity);
        if (expectedBuildIdentity.Length > ControlProtocolConstants.MaxTextFieldChars)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedBuildIdentity));
        }

        if (operationTimeout <= TimeSpan.Zero ||
            operationTimeout == Timeout.InfiniteTimeSpan ||
            operationTimeout.TotalMilliseconds > MaxCancelAfterMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        var endpoint = ControlEndpointIdentity.Create(
            role,
            generation,
            startupOperationId,
            expectedBuildIdentity);
        var pipe = NativeNamedPipeServerFactory.Create(endpoint.PipeName);
        return new WindowsControlChannel(endpoint, pipe, operationTimeout);
    }

    public ControlChildEnvironment CreateChildEnvironment() =>
        new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RoleEnvironmentVariable] = EndpointIdentity.Role.ToString(),
                [GenerationEnvironmentVariable] = EndpointIdentity.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [OperationEnvironmentVariable] = EndpointIdentity.StartupOperationId.ToString("D"),
                [BuildEnvironmentVariable] = EndpointIdentity.ExpectedBuildIdentity,
            },
            new Dictionary<string, SecretValue>(StringComparer.Ordinal)
            {
                [PipeEnvironmentVariable] = new(EndpointIdentity.PipeName),
                [NonceEnvironmentVariable] = new(EndpointIdentity.CapabilityNonce),
            });

    public async Task<IControlChannel> AcceptAsync(
        ISupervisedProcess expectedProcess,
        WindowsJobObject expectedJob,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedProcess);
        ArgumentNullException.ThrowIfNull(expectedJob);
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("This control endpoint generation has already been accepted or faulted.");
        }

        CancellationTokenSource? boundedCancellation = null;
        try
        {
            boundedCancellation = CreateBoundedCancellation(cancellationToken);
            await _pipe.WaitForConnectionAsync(boundedCancellation.Token).ConfigureAwait(false);
            VerifyExpectedGeneration(expectedProcess.Identity);
            _clientProcess = PipeClientIdentityVerifier.Instance.Verify(
                _pipe.SafePipeHandle,
                expectedProcess,
                expectedJob);
            CountFrame();
            var hello = await _readCodec.ReadAsync(_pipe, boundedCancellation.Token).ConfigureAwait(false);
            var expected = expectedProcess.Identity;
            var expectedControlIdentity = new ExpectedControlIdentity(
                EndpointIdentity.Role,
                EndpointIdentity.Generation,
                EndpointIdentity.StartupOperationId,
                expected.ProcessId,
                expected.CreationTimeUtc.UtcTicks,
                EndpointIdentity.CapabilityNonce,
                EndpointIdentity.ExpectedBuildIdentity);
            var validator = new ControlSessionValidator(expectedControlIdentity);
            var validation = validator.Validate(hello);
            if (validation.Status == ControlValidationStatus.Rejected)
            {
                throw new InvalidDataException($"Control Hello rejected: {validation.ReasonCode}.");
            }

            if (AuthenticationPublishHook is { } hook)
            {
                await hook(boundedCancellation.Token).ConfigureAwait(false);
            }

            _validator = validator;
            if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
            {
                throw new ObjectDisposedException(nameof(WindowsControlChannel));
            }

            return this;
        }
        catch
        {
            TransitionToFaulted();
            Interlocked.Exchange(ref _clientProcess, null)?.Dispose();
            _pipe.Dispose();
            throw;
        }
        finally
        {
            boundedCancellation?.Dispose();
        }
    }

    public async Task<ControlEnvelope> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        using var boundedCancellation = CreateBoundedCancellation(cancellationToken);
        var entered = false;
        try
        {
            await _readGate.WaitAsync(boundedCancellation.Token).ConfigureAwait(false);
            entered = true;
            CountFrame();
            var envelope = await _readCodec.ReadAsync(_pipe, boundedCancellation.Token).ConfigureAwait(false);
            EnsureInboundDirection(envelope.Type);
            Validate(envelope);
            return envelope;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            if (entered)
            {
                _readGate.Release();
            }
        }
    }

    public async Task WaitForDisconnectAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var entered = false;
        try
        {
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            // Steady-state supervision is a closure watch, not a protocol
            // heartbeat. It has no operation timeout or frame budget.
            _ = await _readCodec.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException("unexpected-control-frame-during-steady-state");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            if (entered)
            {
                _readGate.Release();
            }
        }
    }

    public async Task SendAsync(ControlEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        EnsureAuthenticated();
        using var boundedCancellation = CreateBoundedCancellation(cancellationToken);
        var entered = false;
        try
        {
            await _writeGate.WaitAsync(boundedCancellation.Token).ConfigureAwait(false);
            entered = true;
            CountFrame();
            EnsureOutboundDirection(envelope.Type);
            Validate(envelope);
            await _writeCodec.WriteAsync(_pipe, envelope, boundedCancellation.Token).ConfigureAwait(false);
            await _pipe.FlushAsync(boundedCancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            if (entered)
            {
                _writeGate.Release();
            }
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
    {
        Task disposal;
        lock (_disposeGate)
        {
            Interlocked.Exchange(ref _state, 4);
            Interlocked.Exchange(ref _clientProcess, null)?.Dispose();
            _pipe.Dispose();
            Interlocked.Exchange(ref _resourcesClosed, 1);
            disposal = _disposeTask ??= Task.CompletedTask;
        }

        try
        {
            disposal.GetAwaiter().GetResult();
        }
        catch (ObjectDisposedException)
        {
            // The synchronous close is authoritative during escalation.
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            Interlocked.Exchange(ref _state, 4);
            Interlocked.Exchange(ref _clientProcess, null)?.Dispose();
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _resourcesClosed, 1);
        }
    }

    private CancellationTokenSource CreateBoundedCancellation(CancellationToken cancellationToken)
    {
        var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(_operationTimeout);
        return bounded;
    }

    private void Validate(ControlEnvelope envelope)
    {
        lock (_validatorGate)
        {
            var validation = _validator!.Validate(envelope);
            if (validation.Status == ControlValidationStatus.Rejected)
            {
                throw new InvalidDataException($"Control message rejected: {validation.ReasonCode}.");
            }
        }
    }

    private void CountFrame()
    {
        if (Interlocked.Increment(ref _frameCount) > ControlProtocolConstants.MaxFramesPerSession)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.FrameLimitExceeded);
        }
    }

    private void EnsureInboundDirection(ControlMessageType type)
    {
        var allowed = type == ControlMessageType.Health || EndpointIdentity.Role switch
        {
            RuntimeRole.Worker => type is
                ControlMessageType.DrainAccepted or
                ControlMessageType.NoNewWorkAcquisition or
                ControlMessageType.ActiveWorkRemaining or
                ControlMessageType.Drained or
                ControlMessageType.ShutdownAccepted or
                ControlMessageType.Stopped,
            RuntimeRole.Server or RuntimeRole.Database => type is
                ControlMessageType.ShutdownAccepted or
                ControlMessageType.Stopped,
            _ => false,
        };
        if (!allowed)
        {
            throw new InvalidDataException($"Control message type {type} is not allowed from the child.");
        }
    }

    private void EnsureOutboundDirection(ControlMessageType type)
    {
        var allowed = type == ControlMessageType.Shutdown ||
            EndpointIdentity.Role == RuntimeRole.Worker && type == ControlMessageType.Drain;
        if (!allowed)
        {
            throw new InvalidDataException($"Control message type {type} is not allowed from the AppHost.");
        }
    }

    private void VerifyExpectedGeneration(SupervisedProcessIdentity identity)
    {
        if (identity.Role != EndpointIdentity.Role ||
            identity.Generation.Role != EndpointIdentity.Role ||
            identity.Generation.Value != EndpointIdentity.Generation ||
            identity.Generation.OperationId != EndpointIdentity.StartupOperationId)
        {
            throw new InvalidOperationException("The control endpoint does not match the supervised process generation.");
        }
    }

    private void EnsureAuthenticated()
    {
        if (Volatile.Read(ref _state) != 2)
        {
            throw new InvalidOperationException("The control channel is not authenticated.");
        }
    }

    private void Fault()
    {
        TransitionToFaulted();
        Interlocked.Exchange(ref _clientProcess, null)?.Dispose();
        _pipe.Dispose();
    }

    private void TransitionToFaulted()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state == 4 || state == 3)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _state, 3, state) == state)
            {
                return;
            }
        }
    }
}
