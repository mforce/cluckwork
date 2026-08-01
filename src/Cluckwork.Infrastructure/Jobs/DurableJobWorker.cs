namespace Cluckwork.Infrastructure.Jobs;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class DurableJob
{
    public Guid Id { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DurableJobStatus Status { get; set; } = DurableJobStatus.Pending;
    public DateTimeOffset RunAfter { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }
    public int Attempts { get; set; }
}

public enum DurableJobStatus { Pending, Running, Completed, Failed }

// Intervals are injectable for the loop-level tests only; DI fills the
// defaults (ActivatorUtilities resolves optional parameters and prefers the
// registered heartbeat singleton).
public sealed class DurableJobWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<DurableJobWorker> logger,
    DurableJobWorkerHeartbeat? heartbeat = null,
    DailyEntryLockSweep? lockSweep = null,
    RefreshTokenPurgeSweep? refreshTokenPurgeSweep = null,
    TimeSpan? pollInterval = null,
    TimeSpan? initialBackoff = null) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly DurableJobWorkerHeartbeat heartbeat = heartbeat ?? new(TimeProvider.System);
    private readonly TimeSpan pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan initialBackoff = initialBackoff ?? TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A transient DB outage must not escape ExecuteAsync: the host default
        // (BackgroundServiceExceptionBehavior.StopHost) would take the whole
        // API down with it (#65). Failures log and retry with capped backoff;
        // StopHost stays as the backstop for anything thrown OUTSIDE the
        // guarded iteration (e.g. a fatal bug in the loop itself).
        //
        // One scheduling mechanism only: success waits the poll interval,
        // failure waits the backoff — a PeriodicTimer on top would stretch
        // every retry back to the poll interval and make the logged backoff
        // a lie (codex review of PR #79). Task.Delay throws on cancellation,
        // which ends the loop as a normal shutdown.
        heartbeat.MarkStarted();
        var backoff = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await TryProcessPendingJobsAsync(stoppingToken))
            {
                heartbeat.MarkSuccessfulPoll();
                backoff = TimeSpan.Zero;
                await Task.Delay(pollInterval, stoppingToken);
                continue;
            }

            backoff = backoff == TimeSpan.Zero
                ? initialBackoff
                : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            logger.LogWarning("Durable job poll failed; next attempt in {Backoff}.", backoff);
            await Task.Delay(backoff, stoppingToken);
        }
    }

    // One guarded poll iteration. Returns false on failure instead of throwing;
    // cancellation propagates so shutdown stays prompt.
    internal async Task<bool> TryProcessPendingJobsAsync(CancellationToken ct)
    {
        try
        {
            await ProcessPendingJobsAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Durable job poll iteration failed.");
            return false;
        }
    }

    private async Task ProcessPendingJobsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var jobs = await db.DurableJobs
            .Where(j => j.Status == DurableJobStatus.Pending && j.RunAfter <= DateTimeOffset.UtcNow)
            .OrderBy(j => j.RunAfter)
            .Take(10)
            .ToListAsync(ct);

        if (jobs.Count > 0)
            logger.LogInformation("Durable job scaffold found {JobCount} pending jobs; no handlers are registered yet", jobs.Count);

        // Recurring sweeps ride the same guarded iteration (null only in the
        // resilience unit tests, which exercise the loop, not the sweeps).
        if (lockSweep is not null)
            await lockSweep.RunAsync(ct);
        if (refreshTokenPurgeSweep is not null)
            await refreshTokenPurgeSweep.RunAsync(ct);
    }
}

public static class DurableJobModelBuilderExtensions
{
    public static void ConfigureDurableJobs(this ModelBuilder builder)
    {
        builder.Entity<DurableJob>(entity =>
        {
            entity.ToTable("durable_jobs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JobType).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.HasIndex(e => new { e.Status, e.RunAfter });
        });
    }
}
