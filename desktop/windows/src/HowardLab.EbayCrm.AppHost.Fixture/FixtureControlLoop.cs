using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Control;

namespace HowardLab.EbayCrm.AppHost.Fixture;

public static class FixtureControlLoop
{
    public static async Task<int> RunAsync(
        FixtureMode mode,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Count != 1 ||
            !int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return 8;
        }

        var pipeName = RequireEnvironment(WindowsControlChannel.PipeEnvironmentVariable);
        var nonce = RequireEnvironment(WindowsControlChannel.NonceEnvironmentVariable);
        var role = Enum.Parse<RuntimeRole>(RequireEnvironment(WindowsControlChannel.RoleEnvironmentVariable));
        var generation = long.Parse(
            RequireEnvironment(WindowsControlChannel.GenerationEnvironmentVariable),
            CultureInfo.InvariantCulture);
        var operationId = Guid.Parse(RequireEnvironment(WindowsControlChannel.OperationEnvironmentVariable));
        var expectedBuild = RequireEnvironment(WindowsControlChannel.BuildEnvironmentVariable);
        var build = ControlProtocolConstants.FixtureBuildIdentity;
        if (!StringComparer.Ordinal.Equals(expectedBuild, build))
        {
            return 11;
        }
        if (role is not (RuntimeRole.Server or RuntimeRole.Worker))
        {
            return 9;
        }

        if (mode == FixtureMode.CrashBeforeHello)
        {
            return 90;
        }

        if (mode == FixtureMode.PipeTimeout)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var healthPayload = new HealthPayload(
            ControlProtocolConstants.CurrentVersion,
            build,
            generation,
            mode == FixtureMode.HealthMismatch ? nonce + "-wrong" : nonce,
            "ready",
            0);
        await using var health = new FixtureHealthServer(port, healthPayload);
        using var process = Process.GetCurrentProcess();
        var hello = new ControlEnvelope(
            ControlProtocolConstants.CurrentVersion,
            operationId,
            role,
            generation,
            ControlMessageType.Hello,
            JsonSerializer.SerializeToElement(
                new HelloPayload(
                    Environment.ProcessId,
                    ProcessCreationTimeTicks.Format(process.StartTime.ToUniversalTime().Ticks),
                    nonce,
                    build,
                    health.Endpoint,
                    "pending-challenge"),
                ControlFrameCodec.SerializerOptions));
        await using var client = new NamedPipeControlClient(pipeName, hello, TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (mode == FixtureMode.CrashAfterHello)
        {
            return 91;
        }

        if (mode == FixtureMode.ControlDisconnect)
        {
            await health.WaitForSuccessfulRequestCountAsync(
                requiredCount: 2,
                cancellationToken).ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        if (mode == FixtureMode.HealthDrop)
        {
            await health.SuccessfulRequest.WaitAsync(cancellationToken).ConfigureAwait(false);
            await health.DisposeAsync().ConfigureAwait(false);
        }
        if (mode is FixtureMode.HealthStaleBuild or
            FixtureMode.HealthStaleProtocol or
            FixtureMode.HealthStaleGeneration or
            FixtureMode.HealthStaleNonce or
            FixtureMode.HealthUnhealthy)
        {
            await health.SuccessfulRequest.WaitAsync(cancellationToken).ConfigureAwait(false);
            health.UpdatePayload(healthPayload with
            {
                ProtocolVersion = mode == FixtureMode.HealthStaleProtocol
                    ? ControlProtocolConstants.CurrentVersion - 1
                    : ControlProtocolConstants.CurrentVersion,
                BuildIdentity = mode == FixtureMode.HealthStaleBuild ? build + "-stale" : build,
                Generation = mode == FixtureMode.HealthStaleGeneration ? generation - 1 : generation,
                GenerationNonce = mode == FixtureMode.HealthStaleNonce ? nonce + "-stale" : nonce,
                Status = mode == FixtureMode.HealthUnhealthy ? "unhealthy" : "ready",
            });
        }

        var drainReplies = new Dictionary<Guid, ControlEnvelope[]>();
        while (true)
        {
            var command = await client.ReadCommandAsync(cancellationToken).ConfigureAwait(false);
            if (command.Type == ControlMessageType.Drain && role == RuntimeRole.Worker)
            {
                if (mode == FixtureMode.DrainDisconnectAfterAccepted)
                {
                    await client.SendAsync(
                        Empty(ControlMessageType.DrainAccepted, command.OperationId, role, generation),
                        cancellationToken).ConfigureAwait(false);
                    await client.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                if (!drainReplies.TryGetValue(command.OperationId, out var replies))
                {
                    replies =
                    [
                        Empty(ControlMessageType.DrainAccepted, command.OperationId, role, generation),
                        Empty(ControlMessageType.NoNewWorkAcquisition, command.OperationId, role, generation),
                        WithPayload(
                            ControlMessageType.ActiveWorkRemaining,
                            command.OperationId,
                            role,
                            generation,
                            new { count = 0 }),
                        Empty(ControlMessageType.Drained, command.OperationId, role, generation),
                    ];
                    drainReplies.Add(command.OperationId, replies);
                }

                foreach (var reply in replies)
                {
                    await client.SendAsync(reply, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (command.Type == ControlMessageType.Shutdown)
            {
                if (mode == FixtureMode.IgnoreShutdown)
                {
                    continue;
                }

                if (mode == FixtureMode.ShutdownDisconnectAfterAccepted)
                {
                    await client.SendAsync(
                        Empty(ControlMessageType.ShutdownAccepted, command.OperationId, role, generation),
                        cancellationToken).ConfigureAwait(false);
                    await client.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                var replies = new[]
                {
                    Empty(ControlMessageType.ShutdownAccepted, command.OperationId, role, generation),
                    Empty(ControlMessageType.Stopped, command.OperationId, role, generation),
                };
                foreach (var reply in replies)
                {
                    await client.SendAsync(reply, cancellationToken).ConfigureAwait(false);
                }

                using var replayWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                replayWindow.CancelAfter(TimeSpan.FromMilliseconds(100));
                try
                {
                    while (true)
                    {
                        var duplicate = await client.ReadAsync(replayWindow.Token).ConfigureAwait(false);
                        if (duplicate.Type != ControlMessageType.Shutdown || duplicate.OperationId != command.OperationId)
                        {
                            return 10;
                        }

                        foreach (var reply in replies)
                        {
                            await client.SendAsync(reply, replayWindow.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (replayWindow.IsCancellationRequested)
                {
                }
                return 0;
            }
        }
    }

    private static ControlEnvelope Empty(
        ControlMessageType type,
        Guid operationId,
        RuntimeRole role,
        long generation) => WithPayload(type, operationId, role, generation, new { });

    private static ControlEnvelope WithPayload<TPayload>(
        ControlMessageType type,
        Guid operationId,
        RuntimeRole role,
        long generation,
        TPayload payload) =>
        new(
            ControlProtocolConstants.CurrentVersion,
            operationId,
            role,
            generation,
            type,
            JsonSerializer.SerializeToElement(payload, ControlFrameCodec.SerializerOptions));

    private static string RequireEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required fixture environment variable '{name}' is unavailable.");
}
