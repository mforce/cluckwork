namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// #123, #179 — the farm logo AND banner's shared table. See FarmLogo for why
// it is not a column on accounts, and for the name discrepancy (this row also
// holds the banner) kept deliberately.
public sealed class FarmLogoConfiguration : IEntityTypeConfiguration<FarmLogo>
{
    public void Configure(EntityTypeBuilder<FarmLogo> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.AccountId).IsRequired();
        builder.Property(l => l.FarmId).IsRequired();

        // --- Logo columns ---
        // All nullable (#179): a row can hold a banner with no logo, so the
        // logo side can no longer be required the way it was pre-#179.

        // bytea. The sanitizer refuses anything larger, so this is a second
        // line rather than the only one — but it is the line the database
        // itself enforces, which is the one that survives a bug upstream.
        builder.Property(l => l.Content).HasColumnType("bytea");

        // "image/png" and friends — the sniffed value, never the client's.
        builder.Property(l => l.ContentType).HasMaxLength(32);
        // Hex SHA-256.
        builder.Property(l => l.ContentHash).HasMaxLength(64);

        // --- Banner columns (#179) --- same shape, same nullability story.

        builder.Property(l => l.BannerContent).HasColumnType("bytea");
        builder.Property(l => l.BannerContentType).HasMaxLength(32);
        builder.Property(l => l.BannerContentHash).HasMaxLength(64);

        // One row per farm, and the index the tenant query filter reads
        // through — AccountId leads for that reason, matching the convention
        // elsewhere in this folder.
        //
        // Unique because the upload handler reads-then-writes: without it two
        // concurrent FIRST uploads would both miss the existing row and insert,
        // and the farm would have two logos with an arbitrary winner on read.
        // With it the second insert fails, which /error already maps to a 409.
        //
        // Concurrent REPLACEMENTS are ordered by the Version token instead — no
        // unique constraint fires when both writers update an existing row.
        //
        // The comment that stood here claimed a token was unnecessary because
        // `Replace` rewrites every field together, so the loser would merely
        // overwrite the winner with a coherent row. That was false: EF writes
        // only the properties that differ from each context's own snapshot, so
        // a second writer whose type/dimensions/length happen to match the
        // ORIGINAL row updates just the bytes and leaves the first writer's
        // metadata describing them. See FarmLogo.Version (codex round 2 of
        // #168).
        builder.HasIndex(l => new { l.AccountId, l.FarmId }).IsUnique();

        builder.Property(l => l.Version).IsConcurrencyToken();

        // The HARD ceiling, not the operational limit: a data-integrity
        // backstop against any write that bypasses the sanitizer. The day-to-
        // day upload cap is config (FarmLogoOptions/FarmBannerOptions),
        // validated to stay at or under the matching ceiling — which lets the
        // cap move without a migration while nothing can store past what the
        // constraint permits (#123, #179).
        //
        // Each side's constraint is conditional on that side being SET
        // (Content/BannerContent IS NOT NULL) — a row holding only a banner
        // has a null Content, which octet_length() would compare against 0 and
        // wrongly fail if the constraint didn't guard on presence first.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_farm_logos_content_length",
            "\"Content\" IS NULL OR " +
            $"(octet_length(\"Content\") > 0 AND octet_length(\"Content\") <= {ImageSanitizer.MaxByteLengthCeiling})"));

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_farm_logos_banner_content_length",
            "\"BannerContent\" IS NULL OR " +
            "(octet_length(\"BannerContent\") > 0 AND octet_length(\"BannerContent\") <= " +
            $"{ImageSanitizer.MaxBannerByteLengthCeiling})"));
    }
}
