namespace Cluckwork.Infrastructure.Jobs;

// Shared heartbeat between the worker and its health check (#65): the worker
// stamps it, the check reads it. Lock-free — stamps are single long writes.
public sealed class DurableJobWorkerHeartbeat(TimeProvider timeProvider)
{
    private long startedAtTicks;
    private long lastSuccessfulPollTicks;

    public void MarkStarted() =>
        Interlocked.Exchange(ref startedAtTicks, timeProvider.GetUtcNow().UtcTicks);

    public void MarkSuccessfulPoll() =>
        Interlocked.Exchange(ref lastSuccessfulPollTicks, timeProvider.GetUtcNow().UtcTicks);

    public DateTimeOffset? StartedAt => Read(ref startedAtTicks);

    public DateTimeOffset? LastSuccessfulPoll => Read(ref lastSuccessfulPollTicks);

    private static DateTimeOffset? Read(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }
}
