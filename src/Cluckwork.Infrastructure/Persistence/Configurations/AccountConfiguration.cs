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
        // data: baked into the migration via HasData (InsertData under the
        // hood), not written by a runtime seeder. Deterministic and
        // multi-instance-safe by construction — every replica applying this
        // migration inserts the exact same row, so there is no seed-order race
        // to reason about. Values mirror what the old DatabaseSeeder /
        // SeedOptions defaults produced (Account.Create(id, "Default Farm",
        // "UTC", "USD")) so an operator upgrading from a boot-seeded database
        // sees no behavioural change — only WHERE the row comes from changed.
        // The currency symbol/minor unit are the literal values
        // CurrencyCatalog.Resolve("USD") returns; HasData needs a static
        // value, so they are spelled out here rather than resolved at
        // migration-generation time. NO credential of any kind is ever seeded
        // on this or any other row (enforced by MigrationSecurityReviewTests).
        builder.HasData(new
        {
            Id = SeedDefaults.AccountId,
            AccountId = SeedDefaults.AccountId,
            Name = "Default Farm",
            // #264's Seed:TimeZoneId config-at-provisioning-time lever is
            // retired along with the runtime seeder it fed (#283) — this row
            // is now a fixed migration literal, so a farm outside UTC sets its
            // real IANA zone via Settings after first login (Account.UpdateSettings
            // already supports this; TimeZoneAvailability.EnsureResolvable still
            // guards the boot-time canary check).
            TimeZoneId = "UTC",
            Locale = Account.DefaultLocale,
            DefaultCurrencyCode = "USD",
            DefaultCurrencySymbol = "$",
            DefaultCurrencyMinorUnit = CurrencyCatalog.DefaultMinorUnit,
            UnitSystem = UnitSystem.Metric,
            FirstDayOfWeek = (DayOfWeek?)null,
            DateFormatOverride = (string?)null,
            TimeFormatOverride = (string?)null,
            Brand = FarmBrands.Default,
            IsActive = true,
            Version = 0,
        });
    }
}
