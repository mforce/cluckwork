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
        new(new ThrowingScopeFactory(exceptionFactory), NullLogger<DurableJobWorker>.Instance,
            new AlwaysLeaderLease());

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
            new AlwaysLeaderLease(),
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

// #65 — the worker heartbeat check: a stalled worker reports Degraded (never
// Unhealthy — a background stall must not pull API traffic).
public sealed class DurableJobWorkerHealthCheckTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext Context = new();

    [Fact]
    public async Task NotStarted_IsDegraded()
    {
        var time = new TestTimeProvider();
        var check = new Cluckwork.Api.HealthChecks.DurableJobWorkerHealthCheck(
            new DurableJobWorkerHeartbeat(time), time);

        var result = await check.CheckHealthAsync(Context);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Started_WithinGrace_IsHealthy_ThenDegradesWithoutPolls()
    {
        var time = new TestTimeProvider();
        var heartbeat = new DurableJobWorkerHeartbeat(time);
        var check = new Cluckwork.Api.HealthChecks.DurableJobWorkerHealthCheck(heartbeat, time);

        heartbeat.MarkStarted();
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            (await check.CheckHealthAsync(Context)).Status);

        // From-boot outage: no successful poll ever — degraded once grace passes.
        time.Now = time.Now.AddSeconds(91);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            (await check.CheckHealthAsync(Context)).Status);
    }

    [Fact]
    public async Task StalePoll_Degrades_AndRecoversOnNextSuccess()
    {
        var time = new TestTimeProvider();
        var heartbeat = new DurableJobWorkerHeartbeat(time);
        var check = new Cluckwork.Api.HealthChecks.DurableJobWorkerHealthCheck(heartbeat, time);

        heartbeat.MarkStarted();
        heartbeat.MarkSuccessfulPoll();
        time.Now = time.Now.AddSeconds(91);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            (await check.CheckHealthAsync(Context)).Status);

        heartbeat.MarkSuccessfulPoll();
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            (await check.CheckHealthAsync(Context)).Status);
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
