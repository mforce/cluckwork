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
        builder.Property(e => e.DefaultCurrencySymbol).HasMaxLength(8);
        builder.Property(e => e.UnitSystem)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(e => e.FirstDayOfWeek)
            .HasConversion<string>()
            .HasMaxLength(16);
        builder.Property(e => e.DateFormatOverride).HasMaxLength(Account.MaxFormatOverrideLength);
        builder.Property(e => e.TimeFormatOverride).HasMaxLength(Account.MaxFormatOverrideLength);

        builder.Property(e => e.Version).IsConcurrencyToken();

        // Derived from the stored symbol/code — not a column.
        builder.Ignore(e => e.CurrencySymbol);
    }
}
