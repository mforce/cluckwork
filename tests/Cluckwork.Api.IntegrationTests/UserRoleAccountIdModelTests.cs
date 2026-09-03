namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// #670 — pins the seam in AppDbContext.OnModelCreating that gives
// IdentityUserRole<Guid> a tenant column the two existing write-side layers
// reach by name. Model-only, no database (AccountIdConcurrencyTokenModelTests
// precedent): the database-side behaviour it buys is proved by
// UserRoleTenantWriteTests, and the migration that carries it to a real
// database by UserRoleAccountIdMigrationTests.
public sealed class UserRoleAccountIdModelTests
{
    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .Options;
        return new AppDbContext(options, new TenantContext(), new FlockScope());
    }

    [Fact]
    public void IdentityUserRole_CarriesAShadowGuidAccountId_ThatIsAConcurrencyToken()
    {
        using var db = BuildContext();

        var userRole = db.Model.FindEntityType(typeof(IdentityUserRole<Guid>))!;
        var accountId = userRole.FindProperty("AccountId");

        Assert.True(accountId is not null, "IdentityUserRole<Guid> has no AccountId property — the seam is missing (#670)");
        Assert.True(accountId.IsShadowProperty(), "AccountId on IdentityUserRole<Guid> must be a SHADOW property — Identity's POCO does not declare it");
        Assert.Equal(typeof(Guid), accountId.ClrType);
        Assert.False(accountId.IsNullable, "AccountId on IdentityUserRole<Guid> must be required");
        // Discovered by the #562 walk, by name — no walk change was made for it.
        Assert.True(accountId.IsConcurrencyToken, "AccountId on IdentityUserRole<Guid> is not a concurrency token — the #562 walk stopped discovering it");
    }

    [Fact]
    public void IdentityUserRole_HasACompositeForeignKeyToApplicationUser_OnIdAndAccountId()
    {
        using var db = BuildContext();

        var userRole = db.Model.FindEntityType(typeof(IdentityUserRole<Guid>))!;
        var composite = userRole.GetForeignKeys()
            .SingleOrDefault(fk => fk.Properties.Select(p => p.Name).SequenceEqual(["UserId", "AccountId"]));

        Assert.True(composite is not null,
            "no foreign key on (UserId, AccountId) — found: " +
            string.Join("; ", userRole.GetForeignKeys().Select(fk => string.Join(",", fk.Properties.Select(p => p.Name)))));
        Assert.Equal(typeof(ApplicationUser), composite.PrincipalEntityType.ClrType);
        Assert.Equal(["Id", "AccountId"], composite.PrincipalKey.Properties.Select(p => p.Name).ToArray());
        Assert.Equal(DeleteBehavior.Cascade, composite.DeleteBehavior);
        // Mirrors ApplicationUser.AccountId → Account: a real key, no navigation.
        Assert.Null(composite.DependentToPrincipal);
        Assert.Null(composite.PrincipalToDependent);
    }
}
