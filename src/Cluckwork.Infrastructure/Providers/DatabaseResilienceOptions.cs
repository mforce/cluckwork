namespace Cluckwork.Infrastructure.Providers;

// #269 — bounds for Npgsql's retrying execution strategy (EnableRetryOnFailure),
// which makes the REQUEST path resilient to a managed-Postgres failover or a
// dropped pooled connection instead of throwing straight to a 500. The
// background worker was already hardened against transient outages in #65
// (DurableJobWorker's own backoff loop); this is the request-path counterpart.
public sealed class DatabaseResilienceOptions
{
    public const string SectionName = "Database:Resilience";

    // Retries a genuinely TRANSIENT failure (Npgsql's own classification —
    // connection loss, "cannot connect now", admin/crash shutdown, etc.; see
    // NpgsqlException.IsTransient / PostgresException.IsTransient) up to this
    // many times before giving up and letting the exception propagate as a
    // 500. Bounded, never infinite: a sustained outage must still fail loudly
    // rather than hang the request forever. A non-transient failure (a
    // constraint violation, a validation error surfaced as an exception,
    // etc.) is never retried regardless of this count.
    public int MaxRetryCount { get; init; } = 6;

    // Ceiling for Npgsql's own jittered exponential backoff between retries.
    public int MaxRetryDelaySeconds { get; init; } = 10;

    // Fail fast at boot rather than surfacing a confusing failure on the
    // first request that hits a transient error.
    public void Validate()
    {
        if (MaxRetryCount < 0)
            throw new InvalidOperationException(
                $"{SectionName}:MaxRetryCount must be >= 0 (was {MaxRetryCount}).");
        if (MaxRetryDelaySeconds <= 0)
            throw new InvalidOperationException(
                $"{SectionName}:MaxRetryDelaySeconds must be greater than 0 (was {MaxRetryDelaySeconds}).");
    }
}
