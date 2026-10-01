namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

// #561 review — the write guard's Modified/Deleted checks compare AccountId's
// ORIGINAL value against the resolved tenant, and that is only meaningful while
// the original value is the DATABASE's.
//
// It stops being the database's the moment an entity reaches SaveChanges
// detached: DbSet.Update and DbSet.Remove attach the instance and seed its
// original values from the caller's own current values, so a hand-built stub
// carrying another tenant's primary key and this tenant's AccountId would pass
// both halves of the check.
//
// Every repository mutation read is a TRACKED read behind the tenant query
// filter, which is what makes the snapshot trustworthy. Until #562 that was
// the whole guarantee; since #562 AccountId is a concurrency token and the
// DATABASE refuses a detached stub's write (DetachedTenantWriteTests), so the
// tracked read is now defence in depth — the layer that keeps the
// interceptor's own check meaningful and a detached write from ever being
// attempted. Flipping one of these reads to AsNoTracking must still fail
// here rather than pass quietly.
//
// TWO tests, because one repository is not the precondition (#561 review round 2):
//   * FlockRepository_GetByIdAsync_ReturnsATrackedEntity proves the MECHANISM
//     against a real database — EF really does hand back a tracked entity with a
//     database snapshot — but for ONE representative only.
//   * AllMutableRepositoryReads_AreTracked walks EVERY repository that can
//     mutate and fails if any of their by-id reads opts out of tracking. Round 2
//     correctly flagged that the first test alone claimed a repository-wide
//     precondition while pinning 1 of 16, so it would not catch the regression it
//     documents. Walk everything, exclude deliberately (AGENTS.md).
//
// The detached-write behaviour itself is asserted in DetachedTenantWriteTests
// (refused, since #562), not here.
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

    // Walks every repository that exposes Update/Remove and asserts none of its
    // single-entity reads opts out of change tracking.
    //
    // Discovery rather than a hand-kept list, deliberately: a new mutable
    // repository is covered the moment it is added, which a list would not do —
    // and a list is the method that produced the 1-of-16 gap in the first place.
    //
    // Deliberate exclusion: reads whose name says ReadOnly. Those exist to serve
    // query paths (DailyEntryRepository.GetReadOnlyAsync,
    // SalesOrderRepository.GetReadOnlyAsync) and are never fed to Update/Remove;
    // AsNoTracking is correct there. The exclusion is by NAME so that adding one
    // is a deliberate, visible act.
    [Fact]
    public void AllMutableRepositoryReads_AreTracked()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Cluckwork.Infrastructure", "Repositories");
        Assert.True(Directory.Exists(dir), $"Repository directory not found: {dir}");

        // The entity a repository can MUTATE is whatever Update/Remove accepts.
        // Keying on that, rather than on "any Get*Async", is what keeps
        // read-only PROJECTIONS out of scope: FarmLogoRepository returns
        // FarmLogoMetadata/FarmLogoContent from AsNoTracking queries and that is
        // correct — they are never handed to Remove. Only reads returning the
        // mutable entity itself feed the write path the interceptor guards.
        var mutates = new Regex(@"public\s+void\s+(?:Update|Remove)\s*\(\s*(?<type>[A-Za-z0-9_]+)\s",
            RegexOptions.Compiled);
        var nextMember = new Regex(@"\n    (?:public|private|internal|protected)\s", RegexOptions.Compiled);

        var mutableRepositories = new List<string>();
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(dir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var entityTypes = mutates.Matches(text)
                .Select(m => m.Groups["type"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (entityTypes.Count == 0) continue;

            var name = Path.GetFileName(file);
            mutableRepositories.Add(name);

            var reads = new List<Match>();
            foreach (var entity in entityTypes)
            {
                var readPattern = new Regex(
                    @"public\s+(?:async\s+)?Task<" + Regex.Escape(entity) +
                    @"\?>\s+(?<name>Get[A-Za-z0-9_]*Async)\s*\(",
                    RegexOptions.Compiled);
                reads.AddRange(readPattern.Matches(text)
                    .Where(m => !m.Groups["name"].Value.Contains("ReadOnly", StringComparison.Ordinal)));
            }

            // Backstop against this guard rotting into a no-op if the
            // repositories are reshaped so the pattern stops matching.
            //
            // Honest about its strength: this assertion is NOT the primary
            // defence and has not been observed firing. Renaming a read is
            // caught earlier and harder by the compiler, because
            // IRepository<T, TId> pins GetByIdAsync — the attempt fails with
            // CS0535 before any test runs. This covers the residue: a mutable
            // repository whose read is NOT interface-bound (FarmLogoRepository's
            // GetTrackedAsync) or a future declaration style the regex misses.
            Assert.True(reads.Count > 0,
                $"{name} mutates [{string.Join(", ", entityTypes)}] but no tracked single-entity read " +
                "was found for it. The guard's pattern no longer matches this file — fix the pattern, " +
                "do not ignore it.");

            foreach (var read in reads)
            {
                var after = text[read.Index..];
                var end = nextMember.Match(after, read.Length);
                var body = end.Success ? after[..end.Index] : after;

                if (body.Contains("AsNoTracking", StringComparison.Ordinal))
                    violations.Add($"{name}: {read.Groups["name"].Value}");
            }
        }

        // Proves the walk actually walked: if discovery breaks, this fails
        // instead of vacuously passing with an empty set.
        Assert.True(mutableRepositories.Count >= 10,
            $"Expected to discover many mutable repositories, found {mutableRepositories.Count}: " +
            string.Join(", ", mutableRepositories));
        Assert.Contains("FlockRepository.cs", mutableRepositories);
        Assert.Contains("CustomerRepository.cs", mutableRepositories);
        Assert.Contains("FarmLogoRepository.cs", mutableRepositories);

        Assert.True(violations.Count == 0,
            "These repositories can mutate, but the read that feeds the write path opts out of change " +
            "tracking. TenantStampInterceptor compares AccountId's ORIGINAL value against the resolved " +
            "tenant, and a detached entity carries caller-seeded originals — the database's AccountId " +
            "token (#562) still refuses the row, but this layer is what keeps that from being reached:\n  " +
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
