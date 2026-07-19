namespace Cluckwork.Api.HealthChecks;

using Cluckwork.Infrastructure.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// A silently stalled job worker (stuck handler, loop death) would otherwise be
// invisible until entries stop locking (#69). Degraded — never Unhealthy — so
// a background stall reports on /health/ready without pulling API traffic
// (Degraded maps to HTTP 200).
public sealed class DurableJobWorkerHealthCheck(
    DurableJobWorkerHeartbeat heartbeat,
    TimeProvider timeProvider) : IHealthCheck
{
    // 3x the worker's 30s poll interval: one missed poll is noise, three is a stall.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var started = heartbeat.StartedAt;
        if (started is null)
            return Task.FromResult(HealthCheckResult.Degraded("Durable job worker has not started."));

        // Before the first successful poll the start stamp is the reference,
        // so a from-boot DB outage degrades once the grace window passes.
        var reference = heartbeat.LastSuccessfulPoll ?? started.Value;
        var age = timeProvider.GetUtcNow() - reference;
        return Task.FromResult(age > StaleAfter
            ? HealthCheckResult.Degraded($"No successful job poll for {age:hh\\:mm\\:ss}.")
            : HealthCheckResult.Healthy());
    }
}
