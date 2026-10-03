using Cluckwork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cluckwork.Infrastructure.Persistence.Configurations;

// ApplicationUser is otherwise mapped by IdentityDbContext's conventions; this
// only bounds the #45 language column. base.OnModelCreating runs before
// ApplyConfigurationsFromAssembly, so this is additive — DisplayName stays text.
public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    // 16 > the 8-char grammar max: headroom without an unbounded text column.
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.Language).HasMaxLength(16);
        builder.Property(u => u.CredentialEpoch).HasDefaultValue(1);
        // #1031 — epoch 0 is retired (#364) and a missing or malformed claim
        // parses to it, so a stored epoch below 1 must be unwritable.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AspNetUsers_CredentialEpoch", "\"CredentialEpoch\" >= 1"));
        builder.Property(u => u.PreferredStepperUnit).HasConversion<string>().HasMaxLength(16);
        builder.Property(u => u.StepUpLogoutEpoch).HasDefaultValue(0);
    }
}
