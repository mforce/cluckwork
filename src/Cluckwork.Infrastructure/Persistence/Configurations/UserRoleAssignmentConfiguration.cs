namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class UserRoleAssignmentConfiguration : IEntityTypeConfiguration<UserRoleAssignment>
{
    public void Configure(EntityTypeBuilder<UserRoleAssignment> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.AccountId).IsRequired();
        builder.Property(a => a.UserId).IsRequired();

        // The scope-check path: one user's assignments.
        builder.HasIndex(a => new { a.AccountId, a.UserId });

        // One row per (user, flock) — duplicates would be noise; a NULL flock
        // (farm/house-wide) is deliberately not unique-constrained since
        // Postgres treats NULLs as distinct anyway.
        builder.HasIndex(a => new { a.UserId, a.FlockId }).IsUnique();
    }
}
