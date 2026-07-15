using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("HowardLab.EbayCrm.AppHost.Core.Tests")]

namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

public sealed class NamedPipeControlClient : IAsyncDisposable
{
    private const string FaultedMessage = "The control client is faulted and cannot be reused.";
    private readonly IControlClientTransport _transport;
    private readonly ControlEnvelope _helloTemplate;
    private readonly TimeSpan _operationTimeout;
    private readonly ControlFrameCodec _codec = new();
    private ClientState _state;

    public NamedPipeControlClient(string pipeName, ControlEnvelope hello, TimeSpan operationTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ValidateArguments(hello, operationTimeout);
        _helloTemplate = hello;
        _operationTimeout = operationTimeout;
        _transport = new NamedPipeClientTransport(pipeName);
    }

    internal NamedPipeControlClient(
        IControlClientTransport transport,
        ControlEnvelope hello,
        TimeSpan operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ValidateArguments(hello, operationTimeout);
        _transport = transport;
        _helloTemplate = hello;
        _operationTimeout = operationTimeout;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanConnect();
        using var boundedCancellation = CreateBoundedCancellation(cancellationToken);
        try
        {
            await _transport.ConnectAsync(boundedCancellation.Token).ConfigureAwait(false);
            var challenge = await _codec.ReadAsync(_transport.Stream, boundedCancellation.Token).ConfigureAwait(false);
            var hello = CreateHelloResponse(challenge);
            await _codec.WriteAsync(_transport.Stream, hello, boundedCancellation.Token).ConfigureAwait(false);
            await _transport.FlushAsync(boundedCancellation.Token).ConfigureAwait(false);
            _state = ClientState.Connected;
        }
        catch
        {
            _state = ClientState.Faulted;
            throw;
        }
    }

    public async Task SendAsync(ControlEnvelope envelope, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        using var boundedCancellation = CreateBoundedCancellation(cancellationToken);
        try
        {
            EnsureDirection(envelope, ControlDirection.ChildToAppHost);
            await _codec.WriteAsync(_transport.Stream, envelope, boundedCancellation.Token).ConfigureAwait(false);
            await _transport.FlushAsync(boundedCancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            _state = ClientState.Faulted;
            throw;
        }
    }

    public async Task<ControlEnvelope> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        using var boundedCancellation = CreateBoundedCancellation(cancellationToken);
        try
        {
            var envelope = await _codec.ReadAsync(_transport.Stream, boundedCancellation.Token).ConfigureAwait(false);
            EnsureInboundDirection(envelope);
            return envelope;
        }
        catch
        {
            _state = ClientState.Faulted;
            throw;
        }
    }

    public async Task<ControlEnvelope> ReadCommandAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        try
        {
            var envelope = await _codec.ReadAsync(_transport.Stream, cancellationToken).ConfigureAwait(false);
            EnsureInboundDirection(envelope);
            return envelope;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _state = ClientState.Faulted;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_state == ClientState.Disposed)
        {
            return;
        }

        _state = ClientState.Disposed;
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private static void ValidateArguments(ControlEnvelope hello, TimeSpan operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (hello.Type != ControlMessageType.Hello)
        {
            throw new ArgumentException("The first control message must be Hello.", nameof(hello));
        }

        if (operationTimeout <= TimeSpan.Zero || operationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
    }

    private ControlEnvelope CreateHelloResponse(ControlEnvelope challengeEnvelope)
    {
        EnsureDirection(challengeEnvelope, ControlDirection.AppHostToChild);
        if (challengeEnvelope.Type != ControlMessageType.IdentityChallenge ||
            challengeEnvelope.Version != _helloTemplate.Version ||
            challengeEnvelope.OperationId != _helloTemplate.OperationId ||
            challengeEnvelope.Role != _helloTemplate.Role ||
            challengeEnvelope.Generation != _helloTemplate.Generation)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.InvalidEnvelope);
        }

        var challenge = ControlFrameCodec.DeserializePayload<IdentityChallengePayload>(challengeEnvelope.Payload);
        var identity = ControlFrameCodec.DeserializePayload<HelloPayload>(_helloTemplate.Payload);
        if (challenge.ProcessId != identity.ProcessId ||
            !string.Equals(
                challenge.ProcessCreationTimeUtcTicks,
                identity.ProcessCreationTimeUtcTicks,
                StringComparison.Ordinal) ||
            !ControlFrameCodec.IsBoundedNonEmpty(challenge.ChallengeId))
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.InvalidPayload);
        }

        var hello = _helloTemplate with
        {
            Payload = JsonSerializer.SerializeToElement(
                identity with { ChallengeId = challenge.ChallengeId },
                ControlFrameCodec.SerializerOptions),
        };
        EnsureDirection(hello, ControlDirection.ChildToAppHost);
        return hello;
    }

    private void EnsureInboundDirection(ControlEnvelope envelope)
    {
        EnsureDirection(envelope, ControlDirection.AppHostToChild);
        if (envelope.Type == ControlMessageType.IdentityChallenge)
        {
            throw new InvalidDataException("A second identity challenge is not allowed after authentication.");
        }
    }

    private void EnsureDirection(ControlEnvelope envelope, ControlDirection direction)
    {
        if (envelope.Role != _helloTemplate.Role ||
            !ControlDirectionPolicy.IsAllowed(_helloTemplate.Role, envelope.Type, direction))
        {
            throw new InvalidDataException(
                $"Control message type {envelope.Type} is not allowed for role {_helloTemplate.Role} in direction {direction}.");
        }
    }

    private CancellationTokenSource CreateBoundedCancellation(CancellationToken cancellationToken)
    {
        var boundedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        boundedCancellation.CancelAfter(_operationTimeout);
        return boundedCancellation;
    }

    private void EnsureCanConnect()
    {
        switch (_state)
        {
            case ClientState.Created:
                return;
            case ClientState.Connected:
                throw new InvalidOperationException("The control client is already connected.");
            case ClientState.Faulted:
                throw new InvalidOperationException(FaultedMessage);
            case ClientState.Disposed:
                throw new ObjectDisposedException(nameof(NamedPipeControlClient));
            default:
                throw new InvalidOperationException();
        }
    }

    private void EnsureConnected()
    {
        switch (_state)
        {
            case ClientState.Connected when _transport.IsConnected:
                return;
            case ClientState.Faulted:
                throw new InvalidOperationException(FaultedMessage);
            case ClientState.Disposed:
                throw new ObjectDisposedException(nameof(NamedPipeControlClient));
            default:
                throw new InvalidOperationException("The control client is not connected.");
        }
    }

    private enum ClientState
    {
        Created,
        Connected,
        Faulted,
        Disposed,
    }
}

internal interface IControlClientTransport : IAsyncDisposable
{
    Stream Stream { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
}

internal sealed class NamedPipeClientTransport : IControlClientTransport
{
    private readonly NamedPipeClientStream _pipe;

    public NamedPipeClientTransport(string pipeName)
    {
        _pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
    }

    public Stream Stream => _pipe;
    public bool IsConnected => _pipe.IsConnected;

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        _pipe.ConnectAsync(cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken) =>
        _pipe.FlushAsync(cancellationToken);

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}
