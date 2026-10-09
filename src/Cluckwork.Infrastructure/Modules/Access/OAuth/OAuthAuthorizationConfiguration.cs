using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;

namespace Cluckwork.Infrastructure.Modules.Access.OAuth;

// #799 — OpenIddict records no use of an authorization, and Connected apps shows when
// each app last acted. Null until the first request; OAuthLastUsedStamp keeps it.
internal sealed class OAuthAuthorizationConfiguration
    : IEntityTypeConfiguration<OpenIddictEntityFrameworkCoreAuthorization<Guid>>
{
    public const string LastUsedAtUtc = nameof(LastUsedAtUtc);

    public void Configure(EntityTypeBuilder<OpenIddictEntityFrameworkCoreAuthorization<Guid>> builder) =>
        builder.Property<DateTimeOffset?>(LastUsedAtUtc);
}
