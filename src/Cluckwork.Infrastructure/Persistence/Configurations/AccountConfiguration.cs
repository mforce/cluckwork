namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// The account row carries the farm's §4.5 localization settings until a farms
// aggregate exists (#123). Everything the settings screen writes is length- and
// concurrency-bounded here.
public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(Account.MaxNameLength).IsRequired();
        builder.Property(e => e.TimeZoneId).HasMaxLength(Account.MaxTimeZoneIdLength).IsRequired();
        builder.Property(e => e.Locale).HasMaxLength(Account.MaxLocaleLength).IsRequired();
        builder.Property(e => e.DefaultCurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(e => e.DefaultCurrencySymbol).HasMaxLength(CurrencyCatalog.MaxSymbolLength);
        builder.Property(e => e.UnitSystem)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(e => e.FirstDayOfWeek)
            .HasConversion<string>()
            .HasMaxLength(16);
        builder.Property(e => e.DefaultStepperUnit)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(e => e.DateFormatOverride).HasMaxLength(Account.MaxFormatOverrideLength);
        builder.Property(e => e.TimeFormatOverride).HasMaxLength(Account.MaxFormatOverrideLength);

        // Stored as the palette id string, not an enum: the same id is written
        // straight into the DOM as data-brand and matched by exact-match CSS,
        // and a retired palette must remain readable rather than break
        // materialisation (#149).
        builder.Property(e => e.Brand)
            .HasMaxLength(FarmBrands.MaxLength)
            .IsRequired();

        builder.Property(e => e.Version).IsConcurrencyToken();

        // Derived from the stored symbol/code — not a column.
        builder.Ignore(e => e.CurrencySymbol);

        // #283 Part 1 — the default single-farm account is static reference
        // data, seeded via idempotent raw SQL in the InitialCreate migration
        // (originally #283's AddBaseReferenceDataAndMustChangePassword,
        // carried by hand through #245's squash), NOT via EF's HasData().
        // HasData would bake the row into the MODEL, so a later model-diff
        // would emit UpdateData/DeleteData against an account the farm has
        // since renamed in Settings. Raw SQL seeds once and then leaves the
        // row alone — see the migration file for the WHERE NOT EXISTS guard
        // (PR #339 review).
    }
}
