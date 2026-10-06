using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.FlockManagement.Flocks;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Api.IntegrationTests;

// The database-backed Flock read checks one real tracked snapshot. The source
// guard checks public Task<TEntity> repository reads for AppDbContext DbSet
// entities and rejects AsNoTracking or AsNoTrackingWithIdentityResolution
// references unless the type/member has a documented read-only purpose. It does not
// prove query filters, collection reads, private helper calls or the context's
// default tracking mode. AccountId's database concurrency token separately
// guards detached writes (#562).
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
        // Repositories/ today, and Modules/<Owner>/Repositories/ once a module moves (#1087).
        var infrastructure = Path.Combine(FindRepoRoot(), "src", "Cluckwork.Infrastructure");
        var repositoryFiles = Directory.GetFiles(infrastructure, "*.cs", SearchOption.AllDirectories)
            .Where(f => IsRepositoryFolder(
                Path.GetRelativePath(infrastructure, Path.GetDirectoryName(f)!).Split(Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(repositoryFiles);

        var entityTypes = typeof(AppDbContext).GetProperties()
            .Select(p => p.PropertyType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(t => t.GetGenericArguments()[0])
            .ToHashSet();
        Assert.True(entityTypes.Count >= 30, $"Expected at least 30 DbSet entities, found {entityTypes.Count}.");

        var expectedReads = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Cluckwork.Infrastructure.", StringComparison.Ordinal) == true
                && IsRepositoryFolder(t.Namespace["Cluckwork.Infrastructure.".Length..].Split('.')))
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

        var entityNames = entityTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var discovered = new HashSet<(string Type, string Member)>();
        var violations = new List<string>();

        foreach (var file in repositoryFiles)
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            Assert.DoesNotContain(root.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
            Assert.DoesNotContain(root.DescendantTrivia(descendIntoTrivia: true),
                trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia));

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!method.Modifiers.Any(SyntaxKind.PublicKeyword)
                    || method.ReturnType is not GenericNameSyntax { Identifier.ValueText: "Task" } task)
                    continue;

                var resultType = task.TypeArgumentList.Arguments.Single();
                if (resultType is NullableTypeSyntax nullable) resultType = nullable.ElementType;
                if (resultType is not IdentifierNameSyntax entity
                    || !entityNames.Contains(entity.Identifier.ValueText))
                    continue;

                var type = method.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText;
                var key = (Type: type, Member: method.Identifier.ValueText);
                Assert.True(discovered.Add(key), $"Duplicate repository read: {key.Type}.{key.Member}");
                if (readOnly.ContainsKey(key)) continue;

                var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                Assert.NotNull(body);
                if (body.DescendantNodes().OfType<SimpleNameSyntax>()
                    .Any(name => name.Identifier.ValueText is "AsNoTracking" or "AsNoTrackingWithIdentityResolution"))
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

    // Segments of a folder below the Infrastructure project, or of a namespace below Cluckwork.Infrastructure;
    // subfolders count, as they did before #1087.
    private static bool IsRepositoryFolder(string[] segments) =>
        segments is ["Repositories", ..] or ["Modules", _, "Repositories", ..];

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cluckwork.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Cluckwork.slnx not found above the test bin directory.");
    }
}
