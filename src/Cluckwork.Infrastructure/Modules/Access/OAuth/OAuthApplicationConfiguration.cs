using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;

namespace Cluckwork.Infrastructure.Modules.Access.OAuth;

// #797 — OpenIddict records no creation time for an application, and expiring an
// unapproved registration needs one. #819's StampCreatedBusinessRecord trigger stamps
// it on insert and keeps it on update, as for every other created-only record.
internal sealed class OAuthApplicationConfiguration
    : IEntityTypeConfiguration<OpenIddictEntityFrameworkCoreApplication<Guid>>
{
    public const string CreatedAtUtc = nameof(CreatedAtUtc);

    public void Configure(EntityTypeBuilder<OpenIddictEntityFrameworkCoreApplication<Guid>> builder) =>
        BusinessRecordModel.ConfigureCreatedTimestamp(builder.Property<DateTimeOffset>(CreatedAtUtc));
}
