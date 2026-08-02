namespace Cluckwork.Infrastructure.Identity;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// Durable, rotating refresh token (tech spec §7.4). The raw token is never stored;
// only its SHA-256 hash. Rotation revokes the presented token and issues a new one,
// so a leaked token is single-use and reuse is detectable.
//
// Deliberately NOT tenant-query-filtered: login and refresh run pre-authentication,
// when TenantContext is unresolved. Lookups are keyed by the unguessable token hash.
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AccountId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    // #364 — stamped explicitly by every known mint site. The database default
    // of zero makes an INSERT from a pre-epoch binary permanently unusable.
    public int IssuedEpoch { get; set; }

    // #176 — set when this token was revoked BY a grace-advance (not a normal
    // rotation). Reuse-detection refuses to grace such a token again, so the
    // idempotency grace is bounded to a SINGLE hop off a normal rotation and
    // cannot be leap-frogged down the chain to extend a stolen session.
    public bool RevokedByGrace { get; set; }

    // #176 — optimistic-concurrency token, regenerated on every rotation. Two
    // concurrent presentations of the same token both load the same stamp; only
    // the first UPDATE (WHERE ConcurrencyStamp = old) matches a row, so the loser
    // throws DbUpdateConcurrencyException and RefreshAsync fails it closed —
    // preventing a fork into two live sessions from one token.
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.UserId).IsRequired();
        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(e => e.ReplacedByTokenHash).HasMaxLength(64);
        builder.Property(e => e.RevokedByGrace).HasDefaultValue(false);
        builder.Property(e => e.IssuedEpoch).HasDefaultValue(0);
        builder.HasIndex(e => e.TokenHash).IsUnique();
        builder.HasIndex(e => e.UserId);
        // #270 — covers RefreshTokenPurgeSweep's delete predicate
        // (WHERE ExpiresAt < cutoff), which would otherwise seq-scan the whole
        // table on every poll.
        builder.HasIndex(e => e.ExpiresAt);

        // #176 — consuming a refresh token (revoke-and-rotate) must be an atomic
        // compare-and-swap: two concurrent presentations of the same token would
        // otherwise both read it active and each mint a live child (a fork that,
        // on the grace path, spawns multiple sessions AND skips theft-detection).
        // The stamp is regenerated on every rotation, so a concurrent writer's
        // UPDATE matches no row and throws DbUpdateConcurrencyException, which
        // RefreshAsync fails closed.
        builder.Property(e => e.ConcurrencyStamp).IsConcurrencyToken().HasMaxLength(36);
    }
}
