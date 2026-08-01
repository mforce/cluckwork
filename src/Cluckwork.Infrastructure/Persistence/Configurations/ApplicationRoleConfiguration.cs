namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// #283 Part 1 — the four assignable roles (Roles.Assignable: Admin/Manager/
// Sales/ReadOnly) are static reference data, baked into the migration via
// HasData exactly like the default account and egg grades: deterministic,
// multi-instance-safe, no runtime seeder (the old DatabaseSeeder's
// SeedAdminRoleAsync — "every assignable role exists up front so role
// assignment never races role creation", #103 — is superseded by this).
// ApplicationRole carries no credential of any kind (Identity's role table has
// no PasswordHash/SecurityStamp column), so this is unconditionally safe to
// seed with a real name (enforced by MigrationSecurityReviewTests).
public sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    // Fixed ids, same convention as SeedDefaults/EggGradeConfiguration — a
    // migration regeneration after a rebase must keep producing the SAME
    // InsertData values.
    private static readonly Guid OwnerRoleId = new("0000000c-0000-0000-0000-000000000001");
    private static readonly Guid ManagerRoleId = new("0000000c-0000-0000-0000-000000000002");
    private static readonly Guid SalesRoleId = new("0000000c-0000-0000-0000-000000000003");
    private static readonly Guid ReadOnlyRoleId = new("0000000c-0000-0000-0000-000000000004");

    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.HasData(
            RoleRow(OwnerRoleId, Roles.Owner),
            RoleRow(ManagerRoleId, Roles.Manager),
            RoleRow(SalesRoleId, Roles.Sales),
            RoleRow(ReadOnlyRoleId, Roles.ReadOnly));
    }

    // NormalizedName matches Identity's default UpperInvariantLookupNormalizer
    // (RoleManager.NormalizeKey) — RoleExistsAsync/FindByNameAsync look rows up
    // by this column, not Name. ConcurrencyStamp is a fixed literal, not
    // Guid.NewGuid(): HasData values must be stable across `dotnet ef
    // migrations add` re-runs (a fresh random guid every regeneration would
    // register as a spurious model change). It carries no security weight for
    // a role row — only Identity's optimistic-concurrency check reads it.
    private static object RoleRow(Guid id, string name) => new
    {
        Id = id,
        Name = name,
        NormalizedName = name.ToUpperInvariant(),
        ConcurrencyStamp = id.ToString(),
    };
}
