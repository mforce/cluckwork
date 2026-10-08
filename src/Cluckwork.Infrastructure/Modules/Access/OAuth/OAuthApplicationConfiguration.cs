using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;

namespace Cluckwork.Infrastructure.Modules.Access.OAuth;

// #797 — OpenIddict records no creation time for an application, and expiring an
// unapproved registration needs one. Stamped by Postgres, never by the API.
internal sealed class OAuthApplicationConfiguration
    : IEntityTypeConfiguration<OpenIddictEntityFrameworkCoreApplication<Guid>>
{
    public const string CreatedAtUtc = nameof(CreatedAtUtc);

    public void Configure(EntityTypeBuilder<OpenIddictEntityFrameworkCoreApplication<Guid>> builder) =>
        builder.Property<DateTimeOffset>(CreatedAtUtc).HasDefaultValueSql("now()");
}
