using HowardLab.EbayCrm.AppHost.Composition;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class OneShotPayloadClosureVerifierTests
{
    [Fact]
    public async Task VerifyAsync_ConcurrentCallersExecuteCallbackExactlyOnce()
    {
        var invocationCount = 0;
        using var start = new ManualResetEventSlim(initialState: false);
        var verifier = new OneShotPayloadClosureVerifier(() =>
        {
            Interlocked.Increment(ref invocationCount);
            Thread.SpinWait(100_000);
        });
        var callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                await verifier.VerifyAsync();
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(callers);

        Assert.Equal(1, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task VerifyAsync_SuccessReplaysSameCompletion()
    {
        var invocationCount = 0;
        var verifier = new OneShotPayloadClosureVerifier(() => invocationCount++);

        var first = verifier.VerifyAsync();
        var second = verifier.VerifyAsync();

        Assert.Same(first, second);
        await first;
        await second;
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task VerifyAsync_CallbackFailureIsSanitizedAndReplayed()
    {
        const string canary = "payload-verifier-secret-canary";
        var callbackError = new AppHostExecutionException(
            canary,
            new InvalidOperationException(canary));
        var verifier = new OneShotPayloadClosureVerifier(() => throw callbackError);

        var first = verifier.VerifyAsync();
        var second = verifier.VerifyAsync();
        var firstError = await Assert.ThrowsAsync<AppHostExecutionException>(() => first);
        var secondError = await Assert.ThrowsAsync<AppHostExecutionException>(() => second);

        Assert.Same(first, second);
        Assert.Same(firstError, secondError);
        Assert.NotSame(callbackError, firstError);
        Assert.Equal("role-payload-trust-failed", firstError.ReasonCode);
        Assert.Equal("role-payload-trust-failed", firstError.Message);
        Assert.DoesNotContain(canary, firstError.ToString(), StringComparison.Ordinal);
        Assert.Null(firstError.InnerException);
    }

    [Fact]
    public void Constructor_NullCallbackFailsWithoutDetail()
    {
        const string canary = "null-callback-secret-canary";

        var error = Assert.Throws<AppHostExecutionException>(() =>
            new OneShotPayloadClosureVerifier(null!));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Equal("role-payload-trust-failed", error.Message);
        Assert.DoesNotContain(canary, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }
}
