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

        // #508 — a durable monotonic ordering key. "Id" is a random v4 Guid
        // (AuditWriter), so it carries no chronology and cannot break a
        // same-instant tie: the wrong actor was being named as the last changer.
        //
        // A SHADOW property, deliberately. AuditEvent is domain data with no
        // mutation surface, and this is a persistence artifact — mapping it on
        // the type would put it in reach of anything that projects the entity.
        // Raw SQL orders by the column directly; LINQ reaches it through
        // EF.Property<long>(e, "Sequence").
        //
        // GENERATED ALWAYS, not BY DEFAULT: Postgres then REFUSES an
        // application-supplied value outright ("cannot insert a non-DEFAULT
        // value into column"), which is what keeps the ordering key
        // unforgeable by application code rather than merely unset by it.
        builder.Property<long>("Sequence")
            .ValueGeneratedOnAdd()
            .UseIdentityAlwaysColumn();

        // Viewer: newest-first per tenant; entity drill-down.
        builder.HasIndex(e => new { e.AccountId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.AccountId, e.EntityId });
    }
}
