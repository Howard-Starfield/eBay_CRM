using System.IO.Pipes;

namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

public sealed class NamedPipeControlClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ControlEnvelope _hello;
    private readonly TimeSpan _operationTimeout;
    private readonly ControlFrameCodec _codec = new();
    private bool _connected;

    public NamedPipeControlClient(string pipeName, ControlEnvelope hello, TimeSpan operationTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(hello);
        if (hello.Type != ControlMessageType.Hello)
        {
            throw new ArgumentException("The first control message must be Hello.", nameof(hello));
        }

        if (operationTimeout <= TimeSpan.Zero || operationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        _hello = hello;
        _operationTimeout = operationTimeout;
        _pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connected)
        {
            throw new InvalidOperationException("The control client is already connected.");
        }

        using var boundedCancellation = CreateBoundedCancellation(cancellationToken);
        await _pipe.ConnectAsync(boundedCancellation.Token).ConfigureAwait(false);
        await _codec.WriteAsync(_pipe, _hello, boundedCancellation.Token).ConfigureAwait(false);
        await _pipe.FlushAsync(boundedCancellation.Token).ConfigureAwait(false);
        _connected = true;
    }

    public async Task SendAsync(ControlEnvelope envelope, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        using var boundedCancellation = CreateBoundedCancellation(cancellationToken);
        await _codec.WriteAsync(_pipe, envelope, boundedCancellation.Token).ConfigureAwait(false);
        await _pipe.FlushAsync(boundedCancellation.Token).ConfigureAwait(false);
    }

    public async Task<ControlEnvelope> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        using var boundedCancellation = CreateBoundedCancellation(cancellationToken);
        return await _codec.ReadAsync(_pipe, boundedCancellation.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }

    private CancellationTokenSource CreateBoundedCancellation(CancellationToken cancellationToken)
    {
        var boundedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        boundedCancellation.CancelAfter(_operationTimeout);
        return boundedCancellation;
    }

    private void EnsureConnected()
    {
        if (!_connected || !_pipe.IsConnected)
        {
            throw new InvalidOperationException("The control client is not connected.");
        }
    }
}
