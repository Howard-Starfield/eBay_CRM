using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;

try
{
    var options = AppHostOptions.Parse(args);
    if (options.Mode == AppHostMode.Probe)
    {
        var result = await AppHostComposition.ProbeAsync(options);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.IsValid ? 0 : 2;
    }

    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stopping.Cancel();
    };
    await using var orchestrator = AppHostComposition.Create(options);
    orchestrator.StateChanged += state => Console.WriteLine(state);
    if (options.Mode == AppHostMode.AcceptanceRunOnce)
    {
        await orchestrator.StartAsync(stopping.Token);
        if (orchestrator.State != RuntimeState.Ready)
        {
            throw new AppHostExecutionException("acceptance-run-not-ready");
        }

        await orchestrator.StopAsync(stopping.Token);
        if (orchestrator.State != RuntimeState.Stopped)
        {
            throw new AppHostExecutionException("acceptance-run-not-stopped");
        }
    }
    else
    {
        await orchestrator.RunUntilStoppedAsync(stopping.Token);
    }

    return 0;
}
catch (Exception error) when (error is AppHostOptionsException or AppHostExecutionException)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
catch (PostgresBinaryLayoutException error)
{
    Console.Error.WriteLine(error.ReasonCode);
    return 2;
}
catch (PostgresClusterRepairRequiredException error)
{
    Console.Error.WriteLine(error.ReasonCode);
    return 2;
}
catch (ProfileRuntimeIdentityException error)
{
    Console.Error.WriteLine(error.ReasonCode);
    return 2;
}
catch (ProfileOwnershipException error)
{
    Console.Error.WriteLine(error.Code);
    return 2;
}
