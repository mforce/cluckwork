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
        builder.HasIndex(e => e.TokenHash).IsUnique();
        builder.HasIndex(e => e.UserId);
    }
}
