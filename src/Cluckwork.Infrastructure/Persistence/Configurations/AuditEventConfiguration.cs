namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.OccurredAtUtc).IsRequired();
        builder.Property(e => e.ActorUserId).IsRequired();
        builder.Property(e => e.ActorEmail).HasMaxLength(AuditEvent.MaxActorEmailLength).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(AuditEvent.MaxActionLength).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(AuditEvent.MaxEntityTypeLength).IsRequired();
        builder.Property(e => e.EntityId).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(AuditEvent.MaxReasonLength);
        // Plain text, not jsonb — provider-portability rule.
        builder.Property(e => e.DetailsJson);

        // Viewer: newest-first per tenant; entity drill-down.
        builder.HasIndex(e => new { e.AccountId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.AccountId, e.EntityId });
    }
}
