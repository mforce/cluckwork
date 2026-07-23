namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// #123 — the farm logo's own table. See FarmLogo for why it is not a column on
// accounts.
public sealed class FarmLogoConfiguration : IEntityTypeConfiguration<FarmLogo>
{
    public void Configure(EntityTypeBuilder<FarmLogo> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.AccountId).IsRequired();
        builder.Property(l => l.FarmId).IsRequired();

        // bytea. The sanitizer refuses anything larger, so this is a second
        // line rather than the only one — but it is the line the database
        // itself enforces, which is the one that survives a bug upstream.
        builder.Property(l => l.Content)
            .HasColumnType("bytea")
            .IsRequired();

        // "image/png" and friends — the sniffed value, never the client's.
        builder.Property(l => l.ContentType).HasMaxLength(32).IsRequired();
        builder.Property(l => l.Width).IsRequired();
        builder.Property(l => l.Height).IsRequired();
        builder.Property(l => l.ByteLength).IsRequired();
        // Hex SHA-256.
        builder.Property(l => l.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(l => l.UpdatedAt).IsRequired();

        // One logo per farm, and the index the tenant query filter reads
        // through — AccountId leads for that reason, matching the convention
        // elsewhere in this folder.
        //
        // Unique because the upload handler reads-then-writes: without it two
        // concurrent FIRST uploads would both miss the existing row and insert,
        // and the farm would have two logos with an arbitrary winner on read.
        // With it the second insert fails, which /error already maps to a 409.
        //
        // It does NOT order two concurrent REPLACEMENTS — no constraint fires
        // when both update an existing row, and there is no concurrency token,
        // so the later commit wins. That is deliberate for branding: a logo has
        // no base version a raw-body PUT could carry, and `Replace` rewrites
        // content, type, dimensions and hash together, so whichever upload wins
        // leaves an internally consistent row. Last-write-wins, never a mix of
        // two images (review of #168).
        builder.HasIndex(l => new { l.AccountId, l.FarmId }).IsUnique();

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_farm_logos_content_length",
            $"octet_length(\"Content\") > 0 AND octet_length(\"Content\") <= {ImageSanitizer.MaxByteLength}"));
    }
}
