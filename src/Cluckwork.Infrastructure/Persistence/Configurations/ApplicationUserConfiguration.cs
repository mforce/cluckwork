namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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
        builder.Property(u => u.PreferredStepperUnit).HasConversion<string>().HasMaxLength(16);
    }
}
