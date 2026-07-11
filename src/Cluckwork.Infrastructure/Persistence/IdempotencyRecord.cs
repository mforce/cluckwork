namespace Cluckwork.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string EndpointHash { get; set; } = string.Empty;
    public string IdempotencyKeyHash { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public static class IdempotencyModelBuilderExtensions
{
    public static void ConfigureIdempotency(this ModelBuilder builder)
    {
        builder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("idempotency_records");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EndpointHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.IdempotencyKeyHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ResponseBody).IsRequired();
            entity.HasIndex(e => new { e.AccountId, e.EndpointHash, e.IdempotencyKeyHash }).IsUnique();
        });
    }
}
