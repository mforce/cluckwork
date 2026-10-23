using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #1276 (G1.2) — pins the AspNetUserTokens seam in AppDbContext.OnModelCreating,
// as UserRoleAccountIdModelTests pins #670's. Model-only, no database.
public sealed class UserTokenAccountIdModelTests
{
    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .Options;
        return new AppDbContext(options, new TenantContext(), new FlockScope());
    }

    [Fact]
    public void IdentityUserToken_CarriesAShadowGuidAccountId_ThatIsAConcurrencyToken()
    {
        using var db = BuildContext();

        var userToken = db.Model.FindEntityType(typeof(IdentityUserToken<Guid>))!;
        var accountId = userToken.FindProperty("AccountId");

        Assert.True(accountId is not null, "IdentityUserToken<Guid> has no AccountId property — the seam is missing (#1276)");
        Assert.True(accountId.IsShadowProperty(), "AccountId on IdentityUserToken<Guid> must be a SHADOW property — Identity's POCO does not declare it");
        Assert.Equal(typeof(Guid), accountId.ClrType);
        Assert.False(accountId.IsNullable, "AccountId on IdentityUserToken<Guid> must be required");
        Assert.True(accountId.IsConcurrencyToken, "AccountId on IdentityUserToken<Guid> is not a concurrency token — the #562 walk stopped discovering it");
    }

    [Fact]
    public void IdentityUserToken_HasACompositeForeignKeyToApplicationUser_OnIdAndAccountId()
    {
        using var db = BuildContext();

        var userToken = db.Model.FindEntityType(typeof(IdentityUserToken<Guid>))!;
        var composite = userToken.GetForeignKeys()
            .SingleOrDefault(fk => fk.Properties.Select(p => p.Name).SequenceEqual(["UserId", "AccountId"]));

        Assert.True(composite is not null,
            "no foreign key on (UserId, AccountId) — found: " +
            string.Join("; ", userToken.GetForeignKeys().Select(fk => string.Join(",", fk.Properties.Select(p => p.Name)))));
        Assert.Equal(typeof(ApplicationUser), composite.PrincipalEntityType.ClrType);
        Assert.Equal(["Id", "AccountId"], composite.PrincipalKey.Properties.Select(p => p.Name).ToArray());
        Assert.Equal(DeleteBehavior.Cascade, composite.DeleteBehavior);
        Assert.Null(composite.DependentToPrincipal);
        Assert.Null(composite.PrincipalToDependent);
    }
}
