namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
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

    // The hosted loop itself: continuous failures must not fault ExecuteTask
    // (StopHost fires off a faulted task), and shutdown must stay prompt even
    // mid-backoff.
    [Fact]
    public async Task HostedLoop_SurvivesContinuousFailures_AndStopsPromptly()
    {
        var worker = new DurableJobWorker(
            new ThrowingScopeFactory(() => new InvalidOperationException("DB is gone")),
            NullLogger<DurableJobWorker>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(5),
            initialBackoff: TimeSpan.FromMilliseconds(5));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(worker.ExecuteTask!.IsFaulted);

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StopAsync(stopTimeout.Token);
        Assert.True(worker.ExecuteTask.IsCompleted);
        Assert.False(worker.ExecuteTask.IsFaulted);
    }
}

// The live/ready split (#65): live runs no checks (process-up only), ready
// includes the database check — healthy here because the test container is
// migrated and reachable. The unhealthy side is covered by the outage drill
// documented on PR #79 (stopping a container inside a test is prohibitively
// slow for the suite).
[Collection(IntegrationCollection.Name)]
public sealed class HealthEndpointTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task LiveAndReady_BothHealthy_WhenDatabaseIsUp()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
