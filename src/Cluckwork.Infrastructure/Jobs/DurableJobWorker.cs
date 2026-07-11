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

public sealed class DurableJobWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<DurableJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessPendingJobsAsync(stoppingToken);
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
