using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Cluckwork.Api.IntegrationTests;

// The database-backed Flock read checks one real tracked snapshot. The source
// guard checks public Task<TEntity> repository reads for AppDbContext DbSet
// entities and rejects explicit AsNoTracking calls unless the type/member has
// a documented read-only purpose. It does not prove query filters, collection
// reads, helper calls or the context's default tracking mode. AccountId's
// database concurrency token separately guards detached writes (#562).
[Collection(IntegrationCollection.Name)]
public sealed class TrackedMutationReadTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task FlockRepository_GetByIdAsync_ReturnsATrackedEntity()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"t-{Guid.NewGuid():N}@test.local");

        var flockId = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var flock = Flock.Create(Guid.NewGuid(), accountId, Guid.NewGuid(), Guid.NewGuid(),
                "Tracked Read Flock", "Breed", DateOnly.FromDateTime(DateTime.UtcNow.Date), 10);
            db.Flocks.Add(flock);
            await db.SaveChangesAsync();
            return flock.Id;
        });

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var repo = scope.ServiceProvider.GetRequiredService<IFlockRepository>();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var loaded = await repo.GetByIdAsync(flockId);

        Assert.NotNull(loaded);
        // Tracked, and Unchanged — i.e. EF holds a real database snapshot for
        // it, which is what the guard's OriginalValue comparison relies on.
        Assert.Equal(EntityState.Unchanged, db2.Entry(loaded!).State);
        Assert.Equal(accountId, db2.Entry(loaded!).Property(nameof(Flock.AccountId)).OriginalValue);
    }

    [Fact]
    public void AllMutableRepositoryReads_AreTracked()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Cluckwork.Infrastructure", "Repositories");
        Assert.True(Directory.Exists(dir), $"Repository directory not found: {dir}");

        var entityTypes = typeof(AppDbContext).GetProperties()
            .Select(p => p.PropertyType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(t => t.GetGenericArguments()[0])
            .ToHashSet();
        Assert.True(entityTypes.Count >= 30, $"Expected at least 30 DbSet entities, found {entityTypes.Count}.");

        var expectedReads = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => t.Namespace == "Cluckwork.Infrastructure.Repositories")
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType.IsGenericType
                    && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                    && entityTypes.Contains(m.ReturnType.GetGenericArguments()[0]))
                .Select(m => (Type: t.Name, Member: m.Name)))
            .ToArray();
        Assert.Equal(expectedReads.Length, expectedReads.Distinct().Count());

        var readOnly = new Dictionary<(string Type, string Member), string>
        {
            [("AccountRepository", "GetCurrentAsync")] =
                "FarmModule projects settings and FarmClock reads the time zone; neither mutates the Account.",
            [("AccountRepository", "GetCurrentSharedLockedAsync")] =
                "Money-row writers take a fresh currency/policy snapshot under FOR SHARE; they mutate other entities.",
            [("AccountRepository", "FindBySlugAsync")] =
                "IdentityProvider resolves the farm code to Id and IsActive before a tenant exists; it never saves this Account.",
            [("DailyEntryRepository", "GetReadOnlyAsync")] =
                "EggOperationsModule projects daily-entry details without mutating the entity.",
            [("SalesOrderRepository", "GetReadOnlyAsync")] =
                "CommerceModule projects order details without mutating the entity.",
            [("FlockRepository", "GetReadOnlyAsync")] =
                "FlockLookup projects lookup details; write handlers use the separate tracked reads.",
            [("FlockRepository", "GetReadOnlyForFlockScopedWriteAsync")] =
                "FlockLookup validates eligibility for writes to other entities; an untracked read avoids a stale pre-transaction snapshot (#1022).",
        };

        var classes = new Regex(@"public\s+sealed\s+class\s+(?<name>[A-Za-z0-9_]+)");
        var reads = new Regex(@"public\s+(?:async\s+)?Task<(?<entity>[A-Za-z0-9_]+)\??>\s+(?<name>[A-Za-z0-9_]+)\s*\(");
        var nextMember = new Regex(@"\n    (?:public|private|internal|protected)\s");
        var noTracking = new Regex(@"\.AsNoTracking(?:WithIdentityResolution)?\s*\(");
        var entityNames = entityTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var discovered = new HashSet<(string Type, string Member)>();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var declarations = classes.Matches(text);
            foreach (Match read in reads.Matches(text))
            {
                if (!entityNames.Contains(read.Groups["entity"].Value)) continue;
                var type = declarations.Last(c => c.Index < read.Index).Groups["name"].Value;
                var key = (Type: type, Member: read.Groups["name"].Value);
                Assert.True(discovered.Add(key), $"Duplicate repository read: {key.Type}.{key.Member}");
                if (readOnly.ContainsKey(key)) continue;

                var after = text[read.Index..];
                var end = nextMember.Match(after, read.Length);
                var body = end.Success ? after[..end.Index] : after;
                if (noTracking.IsMatch(body))
                    violations.Add($"{key.Type}.{key.Member}");
            }
        }

        Assert.True(discovered.SetEquals(expectedReads),
            "Repository read discovery differs from the compiled public Task<DbSet entity> methods. " +
            $"Missing: {string.Join(", ", expectedReads.Except(discovered))}; " +
            $"extra: {string.Join(", ", discovered.Except(expectedReads))}");
        Assert.All(readOnly, exclusion =>
        {
            Assert.Contains(exclusion.Key, discovered);
            Assert.False(string.IsNullOrWhiteSpace(exclusion.Value));
        });
        Assert.True(discovered.Count >= 30, $"Expected at least 30 single-entity reads, found {discovered.Count}.");
        Assert.Contains(("EggUnitConversionRepository", "GetByIdAsync"), discovered);
        Assert.Contains(("ProductRepository", "GetMappingAsync"), discovered);
        Assert.Contains(("AccountRepository", "GetCurrentLockedAsync"), discovered);

        Assert.True(violations.Count == 0,
            "Single-entity repository reads opt out of tracking without a documented read-only purpose:\n  " +
            string.Join("\n  ", violations));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Cluckwork.sln not found above the test bin directory.");
    }
}
