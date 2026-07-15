namespace HowardLab.EbayCrm.AppHost.Composition;

internal static class PayloadPostExitBoundary
{
    private static readonly Task Never = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously).Task;

    internal static Task Start(
        Task nativeExitObservation,
        OneShotPayloadClosureVerifier payloadClosureVerifier,
        IDisposable payloadLifetimeLease)
        => Start(
            nativeExitObservation,
            payloadClosureVerifier,
            payloadLifetimeLease,
            static _ => ValueTask.CompletedTask);

    internal static Task Start(
        Task nativeExitObservation,
        OneShotPayloadClosureVerifier payloadClosureVerifier,
        IDisposable payloadLifetimeLease,
        Func<string, ValueTask> reportFailure)
        => Start(
            nativeExitObservation,
            payloadClosureVerifier,
            payloadLifetimeLease,
            reportFailure,
            static () => { });

    internal static Task Start(
        Task nativeExitObservation,
        OneShotPayloadClosureVerifier payloadClosureVerifier,
        IDisposable payloadLifetimeLease,
        Func<string, ValueTask> reportFailure,
        Action terminalFaultConsumed)
    {
        ArgumentNullException.ThrowIfNull(nativeExitObservation);
        ArgumentNullException.ThrowIfNull(payloadClosureVerifier);
        ArgumentNullException.ThrowIfNull(payloadLifetimeLease);
        ArgumentNullException.ThrowIfNull(reportFailure);
        ArgumentNullException.ThrowIfNull(terminalFaultConsumed);
        var completion = CompleteAsync(
            nativeExitObservation,
            payloadClosureVerifier,
            payloadLifetimeLease,
            reportFailure);
        _ = ConsumeTerminalFaultAsync(completion, terminalFaultConsumed);
        return completion;
    }

    private static async Task CompleteAsync(
        Task nativeExitObservation,
        OneShotPayloadClosureVerifier payloadClosureVerifier,
        IDisposable payloadLifetimeLease,
        Func<string, ValueTask> reportFailure)
    {
        try
        {
            await nativeExitObservation.ConfigureAwait(false);
        }
        catch
        {
            // An authoritative native-observation fault or cancellation is a
            // containment contract breach. Without OS-exit proof, retaining
            // the immutable payload handles indefinitely is the safe failure.
            await Never.ConfigureAwait(false);
            return;
        }

        AppHostExecutionException? failure = null;
        try
        {
            await payloadClosureVerifier.VerifyAsync().ConfigureAwait(false);
        }
        catch
        {
            failure = new AppHostExecutionException("role-payload-trust-failed");
        }
        finally
        {
            try
            {
                payloadLifetimeLease.Dispose();
            }
            catch
            {
                failure ??= new AppHostExecutionException("role-payload-trust-failed");
            }
        }

        if (failure is not null)
        {
            try
            {
                await reportFailure(failure.ReasonCode).ConfigureAwait(false);
            }
            catch
            {
                // Diagnostics cannot replace the sanitized trust failure.
            }

            throw failure;
        }
    }

    private static async Task ConsumeTerminalFaultAsync(
        Task completion,
        Action terminalFaultConsumed)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            try
            {
                terminalFaultConsumed();
            }
            catch
            {
                // The consumer owns terminal observation; test hooks cannot fault it.
            }
        }
    }
}
