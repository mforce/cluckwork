namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

// #532 — a CHEAP FAST-FAIL on the built EF model. It is deliberately NOT the
// guarantee: it cannot catch "model changed, migration never added", because
// tools/schema-docs/generate.sh --check compares the MIGRATED DATABASE to the
// committed docs, so an omitted migration leaves both unchanged and passes.
// AccountScopedIdentityMigrationTests is the guarantee; this is the two-second
// version that fails before Docker even starts.
//
// Why a walk and not "assert the two we know about": adding a composite
// HasIndex does NOT displace Identity's defaults, because the property lists
// differ. Both would survive, and the surviving GLOBAL unique index on
// NormalizedUserName would keep rejecting the second farm's copy of an email.
// Asserting the absence of ANY non-AccountId-led index is what catches that.
public sealed class ApplicationUserIndexModelTests
{
    // Model building never opens a connection, so this needs no database.
    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .Options;
        return new AppDbContext(options, new TenantContext(), new FlockScope());
    }

    private static IEntityType UserEntity(AppDbContext db) =>
        db.Model.FindEntityType(typeof(ApplicationUser))!;

    [Fact]
    public void NoIndexOnApplicationUser_IsLedByAnythingButAccountId()
    {
        using var db = BuildContext();

        var stray = UserEntity(db).GetIndexes()
            .Where(i => i.Properties[0].Name != nameof(ApplicationUser.AccountId))
            .Select(i => string.Join("+", i.Properties.Select(p => p.Name)))
            .ToList();

        Assert.True(stray.Count == 0,
            "Identity's global indexes are still on the model: " + string.Join(", ", stray));
    }

    [Fact]
    public void BothCompositeIdentityIndexes_AreUnique_AndKeepTheirDatabaseNames()
    {
        using var db = BuildContext();
        var indexes = UserEntity(db).GetIndexes().ToList();

        var userName = Assert.Single(indexes, i => i.GetDatabaseName() == "UserNameIndex");
        Assert.True(userName.IsUnique);
        Assert.Equal(
            [nameof(ApplicationUser.AccountId), nameof(ApplicationUser.NormalizedUserName)],
            userName.Properties.Select(p => p.Name));

        var email = Assert.Single(indexes, i => i.GetDatabaseName() == "EmailIndex");
        Assert.True(email.IsUnique);
        Assert.Equal(
            [nameof(ApplicationUser.AccountId), nameof(ApplicationUser.NormalizedEmail)],
            email.Properties.Select(p => p.Name));
    }

    [Fact]
    public void AccountId_HasARestrictingForeignKeyToAccounts_AndNoNavigation()
    {
        using var db = BuildContext();

        var fk = Assert.Single(
            UserEntity(db).GetForeignKeys(),
            f => f.Properties.Count == 1
                && f.Properties[0].Name == nameof(ApplicationUser.AccountId));

        Assert.Equal("Account", fk.PrincipalEntityType.ShortName());
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        // No navigation property: a required navigation from an UNFILTERED
        // dependent to a query-FILTERED principal is a query-time hazard.
        Assert.Null(fk.DependentToPrincipal);
    }

    [Fact]
    public void AccountId_IsImmutableAfterInsert()
    {
        using var db = BuildContext();

        var accountId = UserEntity(db).FindProperty(nameof(ApplicationUser.AccountId))!;

        Assert.Equal(PropertySaveBehavior.Throw, accountId.GetAfterSaveBehavior());
    }

    [Fact]
    public void TheIdentityColumns_AreAllNotNull_OnTheBuiltModel()
    {
        using var db = BuildContext();
        var entity = UserEntity(db);

        var nullable = new[]
        {
            nameof(ApplicationUser.Email),
            nameof(ApplicationUser.NormalizedEmail),
            nameof(ApplicationUser.UserName),
            nameof(ApplicationUser.NormalizedUserName),
        }
        .Where(name => entity.FindProperty(name) is { IsNullable: true })
        .ToList();

        Assert.True(nullable.Count == 0,
            "The identity columns are nullable on the model — RequireUserIdentityColumns "
            + "made them NOT NULL in the database and RequireUserIdentityColumns is what the "
            + "migration pre-checks protect: " + string.Join(", ", nullable));
    }

    [Fact]
    public void ThePrimaryKey_SurvivesTheIndexWalk()
    {
        using var db = BuildContext();

        // GetIndexes() does not include the primary key, so the walk in
        // OnModelCreating cannot disturb it. Verified by probe before this test
        // was written; pinned here so nobody "helpfully" adds a PK carve-out to
        // the walk and changes what it removes.
        var pk = UserEntity(db).FindPrimaryKey()!;

        Assert.Equal([nameof(ApplicationUser.Id)], pk.Properties.Select(p => p.Name));
    }
}
