namespace Cluckwork.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

// #307 — the claim/result states of the database-coordinated idempotency
// protocol. InProgress means a lease is held (LeaseOwner/LeaseExpiresAt are
// meaningful); Completed means the mutation's result was durably published
// and StatusCode/ContentType/ResponseBody are the replay payload.
public enum IdempotencyStatus
{
    InProgress = 0,
    Completed = 1,
}

// #307 — a row is now a CLAIM with a lease, not just a cached response. The
// unique index (AccountId, EndpointHash, IdempotencyKeyHash) is still the
// identity: a claim is acquired via an atomic INSERT (fresh key) or an
// atomic conditional UPDATE (stealing an expired lease on the SAME
// RequestHash — see IdempotencyMiddleware). RequestHash lets a same-key
// replay with a DIFFERENT payload be rejected as a conflict instead of
// either replaying the wrong response or silently re-executing.
public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string EndpointHash { get; set; } = string.Empty;
    public string IdempotencyKeyHash { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public IdempotencyStatus Status { get; set; }

    // Identifies the current lease holder's attempt. Regenerated on every
    // claim and every steal — the guard every claim/steal/publish transition
    // compares against, so a stale attempt can never publish over a newer
    // one's claim.
    public Guid LeaseOwner { get; set; }
    public DateTimeOffset LeaseExpiresAt { get; set; }

    // Populated only once Status is Completed.
    public int? StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

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
            entity.Property(e => e.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ResponseBody);
            entity.HasIndex(e => new { e.AccountId, e.EndpointHash, e.IdempotencyKeyHash }).IsUnique();
        });
    }
}
