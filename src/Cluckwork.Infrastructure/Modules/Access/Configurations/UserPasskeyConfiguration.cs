using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cluckwork.Infrastructure.Modules.Access.Configurations;

// #1282 decision 6: Identity's version 3 passkey table, mapped here instead of
// through Stores.SchemaVersion. IdentityUserContext reads the schema version
// from the application service provider, so a context built with `new` would
// get the version 1 model, and version 3 would also shorten today's key and
// PhoneNumber columns. Mapped here, every construction has the table and the
// default store supports passkeys with the version unset.
//
// AspNetUserTokens' #1276 treatment: a shadow AccountId that
// TenantStampInterceptor stamps and the #562 walk makes a concurrency token,
// and a composite foreign key to AspNetUsers(Id, AccountId), so a row can only
// name the serving farm's user. No query filter: the table stays on #536's
// non-tenant track.
public sealed class UserPasskeyConfiguration : IEntityTypeConfiguration<IdentityUserPasskey<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserPasskey<Guid>> builder)
    {
        builder.ToTable("AspNetUserPasskeys");
        builder.HasKey(p => p.CredentialId);
        // WebAuthn allows at most 1,023 bytes.
        builder.Property(p => p.CredentialId).HasMaxLength(1024);
        builder.OwnsOne(p => p.Data).ToJson();
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.UserId).IsRequired();

        builder.Property<Guid>(nameof(Entity<Guid>.AccountId));
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(nameof(IdentityUserPasskey<Guid>.UserId), nameof(Entity<Guid>.AccountId))
            .HasPrincipalKey(u => new { u.Id, u.AccountId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
