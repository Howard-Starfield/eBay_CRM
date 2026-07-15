using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Fixture;

public sealed class FixtureHealthServer : IAsyncDisposable
{
    private const int MaximumRequestBytes = 8 * 1024;
    private readonly TcpListener _listener;
    private readonly byte[] _payload;
    private readonly HealthPayload _identity;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;
    private readonly SemaphoreSlim _clients = new(8, 8);
    private readonly ConcurrentDictionary<int, Task> _active = new();
    private int _clientId;
    private int _disposed;
    private readonly TaskCompletionSource _successfulRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FixtureHealthServer(int port, HealthPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _payload = JsonSerializer.SerializeToUtf8Bytes(payload, ControlFrameCodec.SerializerOptions);
        _identity = payload;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start(backlog: 8);
        _acceptLoop = AcceptLoopAsync();
    }

    public string Endpoint => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/health";

    internal Task SuccessfulRequest => _successfulRequest.Task;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                if (!_clients.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                var id = Interlocked.Increment(ref _clientId);
                var task = ServeBoundedAsync(client, _stopping.Token);
                _active[id] = task;
                _ = task.ContinueWith(
                    _ => _active.TryRemove(id, out var removed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private async Task ServeBoundedAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                await ServeAsync(client, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
            {
            }
            finally
            {
                _clients.Release();
            }
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
        {
            return;
        }

        await using var stream = client.GetStream();
        var request = new byte[MaximumRequestBytes];
        var count = 0;
        while (count < request.Length)
        {
            var read = await stream.ReadAsync(request.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            count += read;
            if (count >= 4 && request.AsSpan(0, count).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        if (request.AsSpan(0, count).IndexOf("\r\n\r\n"u8) < 0)
        {
            return;
        }

        var requestLineEnd = Array.IndexOf(request, (byte)'\n', 0, count);
        var requestLine = Encoding.ASCII.GetString(
            request,
            0,
            requestLineEnd >= 0 ? requestLineEnd : count).TrimEnd('\r');
        var text = Encoding.ASCII.GetString(request, 0, count);
        var accepted = requestLine == "GET /health HTTP/1.1" &&
            HasHeader(text, "X-AppHost-Protocol", _identity.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) &&
            HasHeader(text, "X-AppHost-Build", _identity.BuildIdentity) &&
            HasHeader(text, "X-AppHost-Generation", _identity.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)) &&
            HasHeader(text, "X-AppHost-Nonce", _identity.GenerationNonce);
        var body = accepted ? _payload : "not found"u8.ToArray();
        var status = accepted ? "200 OK" : "404 Not Found";
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (accepted)
        {
            _successfulRequest.TrySetResult();
        }
    }

    private static bool HasHeader(string request, string name, string expected) =>
        request.Split("\r\n", StringSplitOptions.None).Any(line =>
            StringComparer.Ordinal.Equals(line, $"{name}: {expected}"));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
            await Task.WhenAll(_active.Values).ConfigureAwait(false);
        }
        finally
        {
            _clients.Dispose();
            _stopping.Dispose();
        }
    }
}
