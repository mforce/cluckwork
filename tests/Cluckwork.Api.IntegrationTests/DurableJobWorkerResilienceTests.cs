namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// #65 — a transient failure inside one poll iteration must never escape into
// ExecuteAsync, where the host default (StopHost) would take the API down.
// Plain unit tests: the worker is exercised through its guarded iteration
// with a scope factory that fails the way a dead database does.
public sealed class DurableJobWorkerResilienceTests
{
    private sealed class ThrowingScopeFactory(Func<Exception> exceptionFactory) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw exceptionFactory();
    }

    private static DurableJobWorker Worker(Func<Exception> exceptionFactory) =>
        new(new ThrowingScopeFactory(exceptionFactory), NullLogger<DurableJobWorker>.Instance);

    [Fact]
    public async Task ThrowingIteration_ReturnsFalse_DoesNotPropagate()
    {
        var worker = Worker(() => new InvalidOperationException("DB is gone"));

        Assert.False(await worker.TryProcessPendingJobsAsync(CancellationToken.None));
        // A second poll after the failure is still guarded — no corrupted state.
        Assert.False(await worker.TryProcessPendingJobsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_Propagates_ForPromptShutdown()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var worker = Worker(() => new OperationCanceledException(cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => worker.TryProcessPendingJobsAsync(cts.Token));
    }

    [Fact]
    public async Task CancellationException_WithoutCancelledToken_IsATransientFailure()
    {
        // An OCE surfacing while shutdown was NOT requested (e.g. a provider
        // timeout dressed as cancellation) is an ordinary transient failure.
        var worker = Worker(() => new OperationCanceledException());

        Assert.False(await worker.TryProcessPendingJobsAsync(CancellationToken.None));
    }
}
