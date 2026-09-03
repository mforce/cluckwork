namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #562 — pins the model walk at the end of AppDbContext.OnModelCreating that
// makes AccountId a concurrency token on every entity carrying one.
//
// Model-only, no database (ApplicationUserIndexModelTests precedent): the
// property in question is metadata, and the database-side behaviour it buys
// is proved separately by DetachedTenantWriteTests. Discovery, not a list —
// a new AccountId-bearing entity is covered the moment it is mapped, and
// this test fails if the walk stops covering it.
public sealed class AccountIdConcurrencyTokenModelTests
{
    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .Options;
        return new AppDbContext(options, new TenantContext(), new FlockScope());
    }

    [Fact]
    public void EveryNonKeyAccountId_IsAConcurrencyToken()
    {
        using var db = BuildContext();

        // Selected by NAME and non-key only — deliberately NOT by CLR type.
        // Both layers key on AccountId being a non-nullable Guid (the walk skips
        // any other type, the interceptor skips any other value), so a nullable
        // or converted AccountId would be fail-open in both with every test
        // green if this selector mirrored the walk's own predicate. It names
        // the property instead (#673 tracks closing that by construction).
        var carriers = db.Model.GetEntityTypes()
            .Select(t => (Type: t, AccountId: t.FindProperty("AccountId")))
            .Where(x => x.AccountId is not null && !x.AccountId.IsPrimaryKey())
            .ToList();

        var wrongType = carriers
            .Where(c => c.AccountId!.ClrType != typeof(Guid))
            .Select(c => $"{c.Type.ShortName()}.AccountId ({c.AccountId!.ClrType.Name})")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(wrongType.Count == 0,
            "AccountId must be a non-nullable Guid on every entity that carries it — the write guard and the " +
            "concurrency token both key on exactly that type, and any other is fail-open in both (#673):\n  " +
            string.Join("\n  ", wrongType));

        // Proves the walk walked: a discovery that finds nothing passes vacuously.
        Assert.True(carriers.Count >= 29,
            $"Expected at least 29 AccountId-bearing entity types, found {carriers.Count}: " +
            string.Join(", ", carriers.Select(c => c.Type.ShortName())));
        Assert.Contains(carriers, c => c.Type.ClrType == typeof(DailyEntry));
        Assert.Contains(carriers, c => c.Type.ClrType == typeof(InventoryItem));
        Assert.Contains(carriers, c => c.Type.ClrType == typeof(ApplicationUser));
        Assert.Contains(carriers, c => c.Type.ClrType == typeof(RefreshToken));

        var notTokens = carriers
            .Where(c => !c.AccountId!.IsConcurrencyToken)
            .Select(c => c.Type.ShortName() + ".AccountId")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(notTokens.Count == 0,
            "AccountId must be a concurrency token on every entity that carries it (#562) — the database-side " +
            "tenant check is the WHERE clause it produces. Not a token on:\n  " + string.Join("\n  ", notTokens));
    }

    // The one deliberate exclusion: a primary-key AccountId is already in every
    // WHERE clause, and a token on a key column buys nothing. Pinned so that the
    // exclusion survives as a decision rather than an accident of the walk.
    [Fact]
    public void PrimaryKeyAccountId_OnSimulationSeedState_IsNotAToken()
    {
        using var db = BuildContext();

        var accountId = db.Model.FindEntityType(typeof(SimulationSeedState))!.FindProperty("AccountId")!;

        Assert.True(accountId.IsPrimaryKey());
        Assert.False(accountId.IsConcurrencyToken);
    }
}
