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
using Microsoft.CodeAnalysis.Operations;

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
        foreach (var file in files)
            Assert.False(NamesReadSessionPort(File.ReadAllText(file)), file);
    }

    [Fact]
    public void EndpointInAnActiveFrameworkConditional_NamingAReadSessionPortIsSeen()
    {
        Assert.True(NamesReadSessionPort("""
            class Endpoint
            {
            #if NET10_0
                void Read(IReportQueries queries) { }
            #endif
            }
            """));
        Assert.False(NamesReadSessionPort("class Endpoint { void Read(IInsightsModule insights) { } }"));
    }

    private static bool NamesReadSessionPort(string source)
    {
        var legacyPorts = new[] { nameof(IReportQueries), nameof(IExportQueries), nameof(IAuditEventRepository) };
        return CSharpSyntaxTree.ParseText(source, ModuleLedgerScanner.ParseOptions).GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(n => legacyPorts.Contains(n.Identifier.ValueText));
    }

    [Theory]
    [InlineData("\n#if NET10_0\nawait db.SaveChangesAsync();\n#endif\n")]
    [InlineData("Func<CancellationToken, Task<int>> save = db.SaveChangesAsync; await save(ct);")]
    [InlineData("await Task.Run(db.SaveChanges, ct);")]
    [InlineData("_ = new Cluckwork.Application.Tests.Architecture.InsightsReadOnlyTests.ExternalWriteProbe(db);")]
    [InlineData("Func<FormattableString, IQueryable<Cluckwork.Domain.Auditing.AuditEvent>> sql = db.AuditEvents.FromSqlInterpolated; await sql($\"DELETE FROM audit_events\").ToListAsync();")]
    [InlineData("dynamic alias = db; alias.SaveChanges();")]
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

    public sealed class ExternalWriteProbe
    {
        public ExternalWriteProbe(Cluckwork.Infrastructure.Persistence.AppDbContext db) => db.SaveChanges();
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
            """).Select(s => CSharpSyntaxTree.ParseText(s, ModuleLedgerScanner.ParseOptions)).ToArray();
        var platformPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = platformPaths.Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
            .Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("InsightsReadSessionGuard", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(e => e.ToString())));

        var readData = new HashSet<Type>();
        foreach (var port in ReadPorts)
            foreach (var method in port.GetMethods().Concat(port.GetInterfaces().SelectMany(i => i.GetMethods())))
            {
                AssertDto(method.ReturnType, readData);
                foreach (var parameter in method.GetParameters()) AssertDto(parameter.ParameterType, readData);
            }
        var readDataNames = readData.Select(type => type.FullName).ToHashSet();
        var violations = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var roots = tree.GetRoot().DescendantNodesAndSelf().Select(node => model.GetOperation(node))
                .Where(operation => operation is not null && operation.Parent is null).Distinct();
            foreach (var operation in roots.SelectMany(root => root!.DescendantsAndSelf()))
            {
                if (operation is IDynamicInvocationOperation or IDynamicObjectCreationOperation or IDynamicIndexerAccessOperation)
                {
                    violations.Add("unresolved operation in " + operation.Syntax);
                    continue;
                }
                var method = operation switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod,
                    IMethodReferenceOperation reference => reference.Method,
                    IObjectCreationOperation creation => creation.Constructor,
                    IPropertyReferenceOperation property => (property.Parent is IAssignmentOperation assignment
                        && assignment.Target == property) || property.Parent is IIncrementOrDecrementOperation
                        ? property.Property.SetMethod : property.Property.GetMethod,
                    IConversionOperation conversion => conversion.OperatorMethod,
                    IBinaryOperation binary => binary.OperatorMethod,
                    IUnaryOperation unary => unary.OperatorMethod,
                    IIncrementOrDecrementOperation increment => increment.OperatorMethod,
                    ICompoundAssignmentOperation assignment => assignment.OperatorMethod,
                    _ => null,
                };
                if (method is null) continue;
                var ns = method.ContainingNamespace.ToDisplayString();
                if (ns.StartsWith("Cluckwork.", StringComparison.Ordinal)
                    && !(method.ReducedFrom ?? method).DeclaringSyntaxReferences
                        .Any(reference => trees.Contains(reference.SyntaxTree))
                    && !IsApprovedRead(method, readDataNames))
                    violations.Add(method.ToDisplayString());
                if ((ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                        || ns.StartsWith("Npgsql", StringComparison.Ordinal)
                        || ns.StartsWith("System.Data", StringComparison.Ordinal))
                    && !ReadOperations.Contains(method.Name) && !IsApprovedRead(method, readDataNames))
                    violations.Add(method.ToDisplayString());
                if (method.Name == "FromSqlInterpolated")
                {
                    var sql = operation is IInvocationOperation call
                        ? call.Arguments.Single(argument => argument.Parameter!.Name == "sql").Value.Syntax
                            as InterpolatedStringExpressionSyntax
                        : null;
                    var text = sql is null ? null : string.Concat(sql.Contents
                        .OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText));
                    if (text is null || Regex.IsMatch(text, @"\b(INSERT|UPDATE|DELETE|MERGE|CALL)\b",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        violations.Add("unreviewed or write SQL in " + operation.Syntax);
                }
            }
        }
        return violations;
    }

    private static bool IsApprovedRead(IMethodSymbol method, HashSet<string?> readDataNames)
    {
        var type = method.ContainingType.OriginalDefinition.ToDisplayString();
        if (ReadPorts.Any(port => port.FullName == type)) return true;
        if (method.MethodKind is MethodKind.Constructor or MethodKind.PropertyGet && readDataNames.Contains(type))
            return true;
        if (method.MethodKind == MethodKind.PropertyGet)
        {
            if (method.ContainingAssembly.Name == "Cluckwork.Domain") return true;
            if (type == "Cluckwork.Infrastructure.Persistence.AppDbContext"
                && method.ReturnType.OriginalDefinition.ToDisplayString() == "Microsoft.EntityFrameworkCore.DbSet<TEntity>")
                return true;
        }
        return method.ToDisplayString() is
            "Cluckwork.Infrastructure.Persistence.TenantContext.AccountId.get"
            or "Cluckwork.Infrastructure.Persistence.FlockScope.IsUnrestricted.get"
            or "Cluckwork.Infrastructure.Persistence.AppDbContext.AppDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<Cluckwork.Infrastructure.Persistence.AppDbContext>, Cluckwork.Infrastructure.Persistence.TenantContext, Cluckwork.Infrastructure.Persistence.FlockScope)"
            or "Microsoft.EntityFrameworkCore.DbContext.Database.get"
            or "Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Cluckwork.Infrastructure.Persistence.AppDbContext>.Options.get"
            or "Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Cluckwork.Infrastructure.Persistence.AppDbContext>.DbContextOptionsBuilder()";
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
