using System.Reflection;
using System.Text.RegularExpressions;
using Cluckwork.Application.Features.Audit;
using Cluckwork.Application.Features.Export;
using Cluckwork.Application.Features.Insights;
using Cluckwork.Application.Features.Reports;
using Cluckwork.Application.Tests.TenantBypass;
using Cluckwork.Domain.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class InsightsReadOnlyTests
{
    private static readonly Type[] ReadPorts =
        [typeof(IInsightsModule), typeof(IReportQueries), typeof(IExportQueries), typeof(IAuditEventRepository)];

    private static readonly HashSet<string> ReadOperations =
    [
        "AsNoTracking", "AsAsyncEnumerable", "FromSqlInterpolated", "IgnoreQueryFilters",
        "ToListAsync", "ToDictionaryAsync", "FirstOrDefaultAsync", "SumAsync", "CountAsync", "Property",
        "GetConnectionString", "BeginTransactionAsync", "UseNpgsql", "DisposeAsync",
    ];

    private static string RepoRoot => GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("repo root not found");

    [Fact]
    public void ReadSession_UsesOnlyApprovedPersistenceOperations()
    {
        var files = GuardScanner.EnumerateSourceFiles(
            Path.Combine(RepoRoot, "src", "Cluckwork.Infrastructure", "Insights"));
        Assert.True(files.Count >= 4, "Insights read-session source is missing");
        var violations = FindUnapprovedOperations(files.Select(File.ReadAllText));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void FacadeAndReadPorts_ExposeOnlyDtoReads()
    {
        var seen = new HashSet<Type>();
        foreach (var port in ReadPorts)
        {
            foreach (var method in port.GetMethods().Concat(port.GetInterfaces().SelectMany(i => i.GetMethods())))
            {
                Assert.DoesNotContain("Save", method.Name, StringComparison.Ordinal);
                AssertDto(method.ReturnType, seen);
                foreach (var parameter in method.GetParameters())
                    AssertDto(parameter.ParameterType, seen);
            }
        }
    }

    [Fact]
    public void Endpoints_DoNotUseTheReadSessionPortsDirectly()
    {
        var files = GuardScanner.EnumerateSourceFiles(Path.Combine(RepoRoot, "src", "Cluckwork.Api", "Endpoints"));
        Assert.NotEmpty(files);
        var legacyPorts = new[] { nameof(IReportQueries), nameof(IExportQueries), nameof(IAuditEventRepository) };
        foreach (var file in files)
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            Assert.DoesNotContain(root.DescendantNodes().OfType<IdentifierNameSyntax>(),
                n => legacyPorts.Contains(n.Identifier.ValueText));
        }
    }

    [Theory]
    [InlineData("db.SaveChanges();")]
    [InlineData("await db.SaveChangesAsync();")]
    [InlineData("db.Expenses.Add(null!);")]
    [InlineData("db.Expenses.Remove(null!);")]
    [InlineData("db.Expenses.Update(null!);")]
    [InlineData("await db.Expenses.ExecuteDeleteAsync();")]
    [InlineData("await db.Expenses.ExecuteUpdateAsync(s => s.SetProperty(e => e.Description, \"changed\"));")]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"DELETE FROM expenses\");")]
    [InlineData("db.Entry(new object()).State = EntityState.Modified;")]
    [InlineData("var alias = db; await alias.SaveChangesAsync();")]
    [InlineData("await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, \"DELETE FROM expenses\");")]
    [InlineData("await db.AuditEvents.FromSqlInterpolated($\"WITH removed AS (DELETE FROM audit_events RETURNING *) SELECT * FROM removed\").ToListAsync();")]
    [InlineData("await ((Cluckwork.Application.Common.IUnitOfWork)null!).SaveChangesAsync();")]
    [InlineData("await new UnitOfWork(db).SaveChangesAsync(ct);")]
    [InlineData("await ((Cluckwork.Application.Common.IAuditWriter)null!).WriteAsync(\"Probe.Create\", \"Probe\", Guid.NewGuid());")]
    public void ReadSession_RejectsWritesIncludingAliasesAndStaticCalls(string write)
    {
        var source = $$"""
            using Cluckwork.Infrastructure.Persistence;
            using Microsoft.EntityFrameworkCore;
            internal class Probe(AppDbContext db)
            {
                public async Task Write(CancellationToken ct) { {{write}} await Task.CompletedTask; }
            }
            """;
        Assert.NotEmpty(FindUnapprovedOperations([source]));
    }

    private static IReadOnlyList<string> FindUnapprovedOperations(IEnumerable<string> sources)
    {
        var trees = sources.Append(File.ReadAllText(Path.Combine(RepoRoot, "src",
            "Cluckwork.Infrastructure", "Persistence", "BusinessRecordOrdering.cs"))).Append("""
            global using System;
            global using System.Collections.Generic;
            global using System.Linq;
            global using System.Threading;
            global using System.Threading.Tasks;
            """).Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        var platformPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = platformPaths.Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
            .Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("InsightsReadSessionGuard", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(e => e.ToString())));

        var violations = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                Assert.NotNull(method);
                var ns = method.ContainingNamespace.ToDisplayString();
                if (ns.StartsWith("Cluckwork.Infrastructure", StringComparison.Ordinal)
                    && !(method.ReducedFrom ?? method).DeclaringSyntaxReferences
                        .Any(reference => trees.Contains(reference.SyntaxTree)))
                    violations.Add(method.ToDisplayString());
                if (ns.StartsWith("Cluckwork.Application", StringComparison.Ordinal)
                    && !ReadPorts.Any(p => p.FullName == method.ContainingType.ToDisplayString()))
                    violations.Add(method.ToDisplayString());
                if ((ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                        || ns.StartsWith("Npgsql", StringComparison.Ordinal)
                        || ns.StartsWith("System.Data", StringComparison.Ordinal))
                    && !ReadOperations.Contains(method.Name))
                    violations.Add(method.ToDisplayString());
                if (method.Name == "FromSqlInterpolated")
                {
                    var sql = invocation.ArgumentList.Arguments[0].Expression as InterpolatedStringExpressionSyntax;
                    var text = sql is null ? null : string.Concat(sql.Contents
                        .OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText));
                    if (text is null || Regex.IsMatch(text, @"\b(INSERT|UPDATE|DELETE|MERGE|CALL)\b",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        violations.Add("unreviewed or write SQL in " + invocation);
                }
            }
        }
        return violations;
    }

    private static void AssertDto(Type type, HashSet<Type> seen)
    {
        if (!seen.Add(type)) return;
        Assert.False(typeof(IQueryable).IsAssignableFrom(type), type.ToString());
        Assert.False(type.Namespace?.StartsWith("Cluckwork.Infrastructure", StringComparison.Ordinal) == true, type.ToString());
        for (var current = type; current is not null; current = current.BaseType)
            Assert.False(current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Entity<>), type.ToString());
        if (type.IsArray) AssertDto(type.GetElementType()!, seen);
        foreach (var argument in type.GetGenericArguments()) AssertDto(argument, seen);
        if (type.Namespace?.StartsWith("Cluckwork", StringComparison.Ordinal) == true)
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                AssertDto(property.PropertyType, seen);
    }
}
