namespace Cluckwork.Application.Tests.Architecture;

public sealed class AdapterReachTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("adapter-reach-").FullName;
    private const string Symbol = "Cluckwork.Temp.Endpoints.Endpoint.Run";

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    private void WriteSource(string relativePath, string content)
    {
        var full = Path.Combine(_tempRoot, "src", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string WriteLedger(string adapters = "")
    {
        var path = Path.Combine(_tempRoot, "module-ledger.json");
        File.WriteAllText(path, """
            {
              "owners": {
                "Hub": { "kind": "platform", "namespaces": ["Cluckwork.Temp"] },
                "Farm": { "kind": "module", "namespaces": ["Cluckwork.Temp.Farm"] },
                "FlockManagement": { "kind": "module", "namespaces": [], "exactNamespaces": ["Cluckwork.Temp.Flocks"] }
              },
              "edges": [],
              "adapterRoots": {
                "namespaces": ["Cluckwork.Temp.Endpoints", "Cluckwork.Temp.Cli", "Cluckwork.Temp.Jobs"],
                "types": ["Cluckwork.Temp.Persistence.Seeder"],
                "persistenceForbiddenNamespaces": ["Cluckwork.Temp.Endpoints"]
              },
              "adapters": [
            """ + adapters + "]\n}\n");
        return path;
    }

    private AdapterReachReport Scan(string adapters = "") =>
        AdapterReachScanner.Scan(Path.Combine(_tempRoot, "src"), WriteLedger(adapters));

    private static string Row(string symbol = Symbol, string reaches = "\"Farm\"") =>
        $$"""{ "symbol": "{{symbol}}", "reaches": [{{reaches}}] }""";

    [Fact]
    public void UndeclaredParameter_NamesSymbolOwnerTypeAndLocationAndRendersRow()
    {
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { private void Run(Cluckwork.Temp.Farm.IAccountRepository accounts) { } }
            """);

        var report = Scan();
        var failure = Assert.Single(AdapterReachScanner.Evaluate(report));
        Assert.Contains(Symbol + " -> Farm", failure);
        Assert.Contains("Cluckwork.Temp.Farm.IAccountRepository", failure);
        Assert.Contains("src/Endpoint.cs:2", failure);
        Assert.Contains("\"symbol\": \"" + Symbol + "\"", failure);
        Assert.Contains("\"Farm\"", failure);
        Assert.Empty(AdapterReachScanner.Evaluate(Scan(Row())));
    }

    [Theory]
    [InlineData("services.GetRequiredService<Cluckwork.Temp.Flocks.CreateFlockHandler>()")]
    [InlineData("services.GetService<Cluckwork.Temp.Flocks.CreateFlockHandler>()")]
    [InlineData("services.GetRequiredKeyedService<Cluckwork.Temp.Flocks.CreateFlockHandler>(key)")]
    [InlineData("ActivatorUtilities.CreateInstance<Cluckwork.Temp.Flocks.CreateFlockHandler>(services)")]
    [InlineData("services?.GetService<Cluckwork.Temp.Flocks.CreateFlockHandler>()")]
    public void CliStaticServiceResolution_IsReach(string call)
    {
        WriteSource("Cli.cs", $$"""
            namespace Cluckwork.Temp.Cli;
            public static class Verb { private static void Run() { {{call}}; } }
            """);

        var failure = Assert.Single(AdapterReachScanner.Evaluate(Scan()));
        Assert.Contains("Cluckwork.Temp.Cli.Verb.Run -> FlockManagement", failure);
        Assert.Contains("Cluckwork.Temp.Flocks.CreateFlockHandler", failure);
        Assert.Contains("src/Cli.cs:2", failure);
    }

    [Fact]
    public void GenericArgument_ResolvesThroughUsingAndLongestExactOwnerClaim()
    {
        WriteSource("Flock.cs", "namespace Cluckwork.Temp.Flocks; public class Flock { }");
        WriteSource("Endpoint.cs", """
            using Cluckwork.Temp.Flocks;
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run(IRepository<Flock, Guid> repository) { } }
            """);

        var reach = Assert.Single(Scan().LiveReach);
        Assert.Equal((Symbol, "FlockManagement", "Cluckwork.Temp.Flocks.Flock"), (reach.Symbol, reach.Owner, reach.Type));
    }

    [Theory]
    [InlineData("public Job(Cluckwork.Temp.Farm.Account account) { }", "public class Job { REPLACE }")]
    [InlineData("Cluckwork.Temp.Farm.Account account", "public class Job(REPLACE) { }")]
    public void JobConstructor_IsAdapter(string parameters, string template)
    {
        WriteSource("Job.cs", "namespace Cluckwork.Temp.Jobs; " + template.Replace("REPLACE", parameters, StringComparison.Ordinal));

        var report = Scan();
        Assert.Equal(1, report.WalkedAdapterCount);
        var failure = Assert.Single(AdapterReachScanner.Evaluate(report));
        Assert.Contains("Cluckwork.Temp.Jobs.Job.ctor -> Farm", failure);
    }

    [Theory]
    [InlineData("Cluckwork.Infrastructure.Persistence.AppDbContext")]
    [InlineData("AppDbContext")]
    [InlineData("Microsoft.EntityFrameworkCore.DbContext")]
    [InlineData("DbSet<Cluckwork.Temp.Farm.Account>")]
    [InlineData("IQueryable<Cluckwork.Temp.Farm.Account>")]
    [InlineData("System.Linq.IQueryable")]
    public void EndpointPersistenceParameter_IsForbiddenEvenWhenReachIsDeclared(string type)
    {
        WriteSource("Endpoint.cs", $$"""
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run({{type}} db) { } }
            """);

        var failure = Assert.Single(AdapterReachScanner.Evaluate(Scan(Row())));
        Assert.Contains("forbidden persistence type", failure);
        Assert.Contains(Symbol, failure);
        Assert.Contains("src/Endpoint.cs:2", failure);
    }

    [Fact]
    public void EndpointPersistenceService_IsForbidden()
    {
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run() { services.GetRequiredService<AppDbContext>(); } }
            """);
        Assert.Contains("forbidden persistence type AppDbContext", Assert.Single(AdapterReachScanner.Evaluate(Scan())));
    }

    [Theory]
    [InlineData("Cli")]
    [InlineData("Jobs")]
    [InlineData("Persistence")]
    public void InfrastructurePersistence_DoesNotFailOrCountAsReach(string scope)
    {
        WriteSource("Infrastructure.cs", $$"""
            namespace Cluckwork.Temp.{{scope}};
            public class Seeder(AppDbContext db) {
                public void Run(DbContext context, DbSet<object> set, IQueryable query) {
                    services.GetRequiredService<AppDbContext>();
                }
            }
            """);
        var report = Scan();
        Assert.Equal(2, report.WalkedAdapterCount);
        Assert.Empty(report.LiveReach);
        Assert.Empty(AdapterReachScanner.Evaluate(report));
    }

    [Fact]
    public void RemovedCrossing_StaysGreenAndCanBeAssertedForPruning()
    {
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run(Cluckwork.Temp.Farm.Account account, Cluckwork.Temp.Flocks.Flock flock) { } }
            """);
        var rows = Row(reaches: "\"Farm\", \"FlockManagement\"");
        Assert.Empty(Scan(rows).Loosenable);
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run(Cluckwork.Temp.Farm.Account account) { } }
            """);
        var report = Scan(rows);
        Assert.Empty(AdapterReachScanner.Evaluate(report));
        var loosenable = Assert.Single(report.Loosenable);
        Assert.Equal(Symbol, loosenable.Symbol);
        Assert.Equal(["FlockManagement"], loosenable.Reaches);
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AdapterReachRealTreeTests.AssertNoLoosenable(report));
        Assert.Contains(Symbol + " -> FlockManagement", failure.Message);
    }

    [Theory]
    [InlineData("public void Run() { }")]
    [InlineData("")]
    public void EmptyOrDeletedAdapterRow_IsLoosenable(string member)
    {
        WriteSource("Endpoint.cs", "namespace Cluckwork.Temp.Endpoints; public class Endpoint { " + member + " }");
        var report = Scan(Row());
        Assert.Empty(AdapterReachScanner.Evaluate(report));
        Assert.Equal(["Farm"], Assert.Single(report.Loosenable).Reaches);
    }

    [Fact]
    public void PlatformReach_IsIgnored()
    {
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run(Cluckwork.Temp.Common.Clock clock) { } }
            """);
        var report = Scan();
        Assert.Empty(report.LiveReach);
        Assert.Empty(AdapterReachScanner.Evaluate(report));
    }

    [Theory]
    [InlineData("\"Unknown\"", "unknown owner 'Unknown'")]
    [InlineData("\"Hub\"", "platform owner 'Hub'")]
    public void InvalidReachOwner_IsRegistryError(string reaches, string expected)
    {
        WriteSource("Endpoint.cs", "namespace Cluckwork.Temp.Endpoints; public class Endpoint { }");
        Assert.Contains(Scan(Row(reaches: reaches)).RegistryErrors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateAndBlankSymbols_AreRegistryErrors()
    {
        WriteSource("Endpoint.cs", "namespace Cluckwork.Temp.Endpoints; public class Endpoint { }");
        var report = Scan(Row() + "," + Row() + "," + Row(symbol: " "));
        Assert.Contains(report.RegistryErrors, e => e.Contains("duplicate adapter symbol", StringComparison.Ordinal));
        Assert.Contains(report.RegistryErrors, e => e.Contains("blank", StringComparison.Ordinal));
        Assert.NotEmpty(AdapterReachScanner.Evaluate(report));
    }

    [Fact]
    public void TempFloor_IsItsOwnCountAndHigherFloorFails()
    {
        WriteSource("Endpoint.cs", "namespace Cluckwork.Temp.Endpoints; public class Endpoint { private void Run() { } }");
        var report = Scan();
        Assert.Equal(1, report.WalkedAdapterCount);
        Assert.Equal(1, report.ExpectedAdapterCountFloor);
        Assert.Empty(AdapterReachScanner.Evaluate(report));
        Assert.Contains("walked 1 adapters, expected at least 40",
            Assert.Single(AdapterReachScanner.Evaluate(report with { ExpectedAdapterCountFloor = 40 })));
    }

    [Fact]
    public void ParseErrors_OutsideAdapterRootsAlsoMakeTheWalkUntrusted()
    {
        WriteSource("Broken.cs", "namespace Cluckwork.Temp.Other; public class Broken {");
        Assert.Contains("the walk cannot be trusted", Assert.Single(AdapterReachScanner.Evaluate(Scan())));
    }

    [Fact]
    public void SameNamespaceAndSameFileTypes_AreResolvedWithoutUsings()
    {
        WriteSource("Account.cs", "namespace Cluckwork.Temp.Farm; public class Account { }");
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Farm {
                public class Seeder { private void Run(Account account) { } }
            }
            """);
        var path = WriteLedger();
        File.WriteAllText(path, File.ReadAllText(path).Replace("Cluckwork.Temp.Persistence.Seeder", "Cluckwork.Temp.Farm.Seeder", StringComparison.Ordinal));
        var report = AdapterReachScanner.Scan(Path.Combine(_tempRoot, "src"), path);
        Assert.Equal("Cluckwork.Temp.Farm.Account", Assert.Single(report.LiveReach).Type);
    }

    [Fact]
    public void NamespaceScopedImports_DoNotLeakAndUnmatchedSimpleNamesAreReported()
    {
        WriteSource("Account.cs", "namespace Cluckwork.Temp.Farm; public class Account { }");
        WriteSource("Endpoints.cs", """
            namespace Cluckwork.Temp.Endpoints.One {
                using Cluckwork.Temp.Farm;
                public class Endpoint { public void Run(Account account) { } }
            }
            namespace Cluckwork.Temp.Endpoints.Two {
                public class Endpoint { public void Run(Account account) { } }
            }
            """);
        var report = Scan();
        Assert.Equal("Cluckwork.Temp.Endpoints.One.Endpoint.Run", Assert.Single(report.LiveReach).Symbol);
        Assert.Contains("Cluckwork.Temp.Endpoints.Two.Endpoint.Run: Account", Assert.Single(report.UnresolvedTypes));
    }

    [Fact]
    public void PrivateNestedMethodsAndOverloads_AreWalkedButLambdasAreNotAdapters()
    {
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint {
                private void Run(Cluckwork.Temp.Farm.Account account) { Action work = () => { services.GetService<Cluckwork.Temp.Flocks.Flock>(); }; }
                private void Run() { }
                private class Nested { void Help(Cluckwork.Temp.Farm.Account account) { } }
            }
            """);
        var report = Scan();
        Assert.Equal(3, report.WalkedAdapterCount);
        Assert.Equal(["Farm", "FlockManagement"], report.LiveReach.Where(r => r.Symbol == Symbol).Select(r => r.Owner));
        Assert.Contains(report.LiveReach, r => r.Symbol == "Cluckwork.Temp.Endpoints.Endpoint.Nested.Help");
    }

    [Fact]
    public void GenericEnclosingTypes_HaveDistinctAdapterSymbols()
    {
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run(Cluckwork.Temp.Farm.Account account) { } }
            public class Endpoint<T> { public void Run(Cluckwork.Temp.Flocks.Flock flock) { } }
            """);
        var report = Scan();
        Assert.Equal(["Cluckwork.Temp.Endpoints.Endpoint.Run", "Cluckwork.Temp.Endpoints.Endpoint<>.Run"],
            report.LiveReach.Select(r => r.Symbol));
        Assert.Equal(2, report.WalkedAdapterCount);
    }

    [Theory]
    [InlineData("Accounts", "Farm")]
    [InlineData("global::Cluckwork.Temp.Flocks.Flock", "FlockManagement")]
    [InlineData("Temp.Farm.Account", "Farm")]
    [InlineData("FarmAlias.Account", "Farm")]
    public void AliasesAndRelativeAndGlobalQualifiedNames_ReachTheirOwners(string type, string owner)
    {
        WriteSource("Endpoint.cs", $$"""
            using Accounts = System.Collections.Generic.List<Cluckwork.Temp.Farm.Account>;
            using FarmAlias = Cluckwork.Temp.Farm;
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run({{type}} value) { } }
            """);
        var reach = Assert.Single(Scan().LiveReach);
        Assert.Equal(owner, reach.Owner);
        Assert.Equal(Symbol, reach.Symbol);
    }
}
