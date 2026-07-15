namespace HowardLab.EbayCrm.AppHost.Composition;

internal sealed class OneShotPayloadClosureVerifier
{
    private const string FailureReason = "role-payload-trust-failed";

    private readonly Lazy<Task> _completion;

    internal OneShotPayloadClosureVerifier(Action callback)
    {
        if (callback is null)
        {
            throw Failure();
        }

        _completion = new Lazy<Task>(
            () => Invoke(callback),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal Task VerifyAsync() => _completion.Value;

    private static Task Invoke(Action callback)
    {
        try
        {
            callback();
            return Task.CompletedTask;
        }
        catch
        {
            return Task.FromException(Failure());
        }
    }

    private static AppHostExecutionException Failure() => new(FailureReason);
}
