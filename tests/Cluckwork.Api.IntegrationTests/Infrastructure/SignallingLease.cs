using Cluckwork.Infrastructure.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// #1202 — a lease with a scripted answer that reports when the worker asks a second
// time. The loop is sequential, so a second acquisition means the first iteration
// (the poll, the sweeps or the heartbeat stamp) has finished and its effects are
// visible. On .NET 10, BackgroundService.StartAsync returns before ExecuteAsync runs
// its first iteration, so a fixed delay after StartAsync raced a starved thread pool.
internal sealed class SignallingLease(Func<LeaseStatus> acquire) : ILeaderLease
{
    private readonly TaskCompletionSource secondAcquisition =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int acquisitions;

    public SignallingLease(LeaseStatus status) : this(() => status) { }

    public Task<LeaseStatus> TryAcquireAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref acquisitions) == 2)
            secondAcquisition.TrySetResult();
        return Task.FromResult(acquire());
    }

    // Starts the worker, waits for its first iteration to complete, then stops it.
    // The timeout bounds a hung worker; it never paces the test.
    public async Task RunWorkerThroughOneIterationAsync(DurableJobWorker worker)
    {
        await worker.StartAsync(CancellationToken.None);
        await secondAcquisition.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StopAsync(stopTimeout.Token);
    }
}

// A host whose own DurableJobWorker is removed, for tests that run the worker or a
// sweep themselves and assert on the rows: the host's leader would otherwise sweep
// the same rows on its own poll and race the test.
public sealed class NoHostWorkerFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.Remove(services.Single(
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(DurableJobWorker))));
    }
}
