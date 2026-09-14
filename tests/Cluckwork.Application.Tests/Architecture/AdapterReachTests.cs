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
                "topLevelPrograms": ["Cluckwork.Temp.Api"],
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

    [Theory]
    [InlineData("MapGet")]
    [InlineData("MapPost")]
    [InlineData("MapPut")]
    [InlineData("MapDelete")]
    [InlineData("MapPatch")]
    [InlineData("MapMethods")]
    public void TypedInlineLambdaParameter_IsReachOfTheMappingMethod(string map)
    {
        WriteSource("Endpoint.cs", $$"""
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint {
                public void Run() {
                    group.{{map}}("/", (Cluckwork.Temp.Farm.Account account) => account);
                }
            }
            """);
        var report = Scan();
        Assert.Equal(1, report.WalkedAdapterCount);
        var failure = Assert.Single(AdapterReachScanner.Evaluate(report));
        Assert.Contains(Symbol + " -> Farm", failure);
        Assert.Contains("Cluckwork.Temp.Farm.Account", failure);
        Assert.Contains("src/Endpoint.cs:4", failure);
    }

    [Fact]
    public void TypedInlineLambdaPersistenceParameter_IsForbiddenAtTheMappingMethod()
    {
        WriteSource("Endpoint.cs", """
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint {
                public void Run() {
                    group.MapPost("/", (Cluckwork.Infrastructure.Persistence.AppDbContext db) => db);
                }
            }
            """);
        var failure = Assert.Single(AdapterReachScanner.Evaluate(Scan(Row())));
        Assert.Contains("forbidden persistence type Cluckwork.Infrastructure.Persistence.AppDbContext", failure);
        Assert.Contains(Symbol, failure);
        Assert.Contains("src/Endpoint.cs:4", failure);
    }

    [Theory]
    [InlineData("account => account")]
    [InlineData("(account) => account")]
    public void UntypedInlineLambdaParameter_IsIgnored(string lambda)
    {
        WriteSource("Endpoint.cs", $$"""
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run() { group.MapGet("/", {{lambda}}); } }
            """);
        var report = Scan();
        Assert.Equal(1, report.WalkedAdapterCount);
        Assert.Empty(report.LiveReach);
        Assert.Empty(report.UnresolvedTypes);
        Assert.Empty(AdapterReachScanner.Evaluate(report));
    }

    [Theory]
    [InlineData("GetService")]
    [InlineData("GetRequiredService")]
    [InlineData("GetKeyedService")]
    [InlineData("GetRequiredKeyedService")]
    public void NonGenericServiceResolution_RecordsReachAndRejectsEndpointPersistence(string service)
    {
        var key = service.Contains("Keyed", StringComparison.Ordinal) ? ", key" : "";
        WriteSource("Endpoint.cs", $$"""
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint {
                public void Run() {
                    services.{{service}}(typeof(Cluckwork.Temp.Farm.Account){{key}});
                    var db = (AppDbContext)context.HttpContext.RequestServices.{{service}}(typeof(AppDbContext){{key}});
                }
            }
            """);
        var report = Scan();
        var reach = Assert.Single(report.LiveReach);
        Assert.Equal((Symbol, "Farm", "Cluckwork.Temp.Farm.Account", 4),
            (reach.Symbol, reach.Owner, reach.Type, reach.Line));
        var persistence = Assert.Single(report.PersistenceViolations);
        Assert.Contains("forbidden persistence type AppDbContext", persistence);
        Assert.Contains(Symbol, persistence);
        Assert.Contains("src/Endpoint.cs:5", persistence);
        Assert.Equal(2, AdapterReachScanner.Evaluate(report).Count);
    }

    [Fact]
    public void GenericOptionalKeyedService_IsReach()
    {
        WriteSource("Cli.cs", """
            namespace Cluckwork.Temp.Cli;
            public class Verb { public void Run() { services.GetKeyedService<Cluckwork.Temp.Flocks.Flock>(key); } }
            """);
        var failure = Assert.Single(AdapterReachScanner.Evaluate(Scan()));
        Assert.Contains("Cluckwork.Temp.Cli.Verb.Run -> FlockManagement", failure);
        Assert.Contains("Cluckwork.Temp.Flocks.Flock", failure);
        Assert.Contains("src/Cli.cs:2", failure);
    }

    [Theory]
    [InlineData("GetServices<Cluckwork.Temp.Farm.Account>()")]
    [InlineData("GetServices(typeof(Cluckwork.Temp.Farm.Account))")]
    [InlineData("GetKeyedServices<Cluckwork.Temp.Farm.Account>(key)")]
    [InlineData("GetKeyedServices(typeof(Cluckwork.Temp.Farm.Account), key)")]
    public void CollectionResolver_RecordsReachAndEndpointPersistence(string call)
    {
        WriteSource("Endpoint.cs", $$"""
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run() {
                services.{{call}};
                services.{{call.Replace("Cluckwork.Temp.Farm.Account", "AppDbContext", StringComparison.Ordinal)}};
            } }
            """);
        var report = Scan();
        Assert.Equal("Farm", Assert.Single(report.LiveReach).Owner);
        Assert.Contains("forbidden persistence type AppDbContext", Assert.Single(report.PersistenceViolations));
        Assert.Equal(2, AdapterReachScanner.Evaluate(report).Count);
    }

    [Theory]
    [InlineData("ActivatorUtilities.GetServiceOrCreateInstance<Cluckwork.Temp.Farm.Account>(sp)")]
    [InlineData("ActivatorUtilities.GetServiceOrCreateInstance(sp, typeof(Cluckwork.Temp.Farm.Account))")]
    [InlineData("Microsoft.Extensions.DependencyInjection.ActivatorUtilities.GetServiceOrCreateInstance(sp, typeof(Cluckwork.Temp.Farm.Account))")]
    public void ActivatorResolver_RecordsReachAndEndpointPersistence(string call)
    {
        WriteSource("Endpoint.cs", $$"""
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run() {
                {{call}};
                {{call.Replace("Cluckwork.Temp.Farm.Account", "AppDbContext", StringComparison.Ordinal)}};
            } }
            """);
        var report = Scan();
        Assert.Equal("Farm", Assert.Single(report.LiveReach).Owner);
        Assert.Contains("forbidden persistence type AppDbContext", Assert.Single(report.PersistenceViolations));
    }

    [Theory]
    [InlineData("GetServiceOrCreateInstance<AppDbContext>(sp)")]
    [InlineData("GetServiceOrCreateInstance(sp, typeof(AppDbContext))")]
    public void StaticallyImportedActivatorResolver_RejectsEndpointPersistence(string call)
    {
        WriteSource("Endpoint.cs", $$"""
            using static Microsoft.Extensions.DependencyInjection.ActivatorUtilities;
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run() { {{call}}; } }
            """);
        Assert.Contains("AppDbContext in " + Symbol, Assert.Single(Scan().PersistenceViolations));
    }

    [Theory]
    [InlineData("void Handler(Cluckwork.Temp.Farm.Account account, AppDbContext db) { }")]
    [InlineData("var handler = delegate(Cluckwork.Temp.Farm.Account account, AppDbContext db) { };")]
    public void LocalFunctionAndAnonymousMethodParameters_BelongToEnclosingAdapter(string handler)
    {
        WriteSource("Endpoint.cs", $$"""
            namespace Cluckwork.Temp.Endpoints;
            public class Endpoint { public void Run() { {{handler}} } }
            """);
        var report = Scan();
        Assert.Equal(1, report.WalkedAdapterCount);
        Assert.Equal(Symbol, Assert.Single(report.LiveReach).Symbol);
        Assert.Contains("AppDbContext in " + Symbol, Assert.Single(report.PersistenceViolations));
    }

    [Theory]
    [InlineData("MapGet")]
    [InlineData("MapPost")]
    [InlineData("MapPut")]
    [InlineData("MapDelete")]
    [InlineData("MapPatch")]
    [InlineData("MapMethods")]
    [InlineData("MapFallback")]
    [InlineData("Map")]
    public void TopLevelRouteLambda_IsEndpointAdapterButCompositionIsNot(string map)
    {
        var methods = map == "MapMethods" ? "new[] { \"GET\" }, " : "";
        WriteSource("Cluckwork.Temp.Api/Program.cs", $$"""
            services.GetRequiredService<AppDbContext>();
            services.GetRequiredService<Cluckwork.Temp.Flocks.Flock>();
            app.{{map}}("/api/v1/x", {{methods}}(Cluckwork.Temp.Farm.Account account, AppDbContext db) => {
                services.GetServices<Cluckwork.Temp.Farm.Account>();
                return account;
            });
            """);
        var report = Scan();
        Assert.Equal(1, report.WalkedAdapterCount);
        var symbol = map == "MapMethods"
            ? "Cluckwork.Temp.Api.Program.MapMethods(/api/v1/x;[GET])"
            : $"Cluckwork.Temp.Api.Program.{map}(/api/v1/x)";
        Assert.All(report.LiveReach, r => Assert.Equal(symbol, r.Symbol));
        Assert.Equal(["Farm"], report.LiveReach.Select(r => r.Owner).Distinct());
        var failure = Assert.Single(report.PersistenceViolations);
        Assert.Contains("AppDbContext in " + symbol, failure);
        Assert.Contains("src/Cluckwork.Temp.Api/Program.cs:3", failure);
    }

    [Theory]
    [InlineData("app.MapGet(\"/x\", Handler);", "Cluckwork.Temp.Api.Program.MapGet(/x).Handler")]
    [InlineData("app.MapFallback(Handler);", "Cluckwork.Temp.Api.Program.MapFallback().Handler")]
    public void TopLevelLocalFunctionHandler_IsResolvedWithoutScanningOtherLocalFunctions(string mapping, string symbol)
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", $$"""
            {{mapping}}
            void Handler(Cluckwork.Temp.Farm.Account account, AppDbContext db) {
                services.GetServices<Cluckwork.Temp.Farm.Account>();
            }
            void Configure(Cluckwork.Temp.Flocks.Flock flock, DbContext db) { }
            """);
        var report = Scan();
        Assert.Equal(1, report.WalkedAdapterCount);
        Assert.All(report.LiveReach, r => Assert.Equal(symbol, r.Symbol));
        Assert.Equal(["Farm"], report.LiveReach.Select(r => r.Owner).Distinct());
        Assert.Contains("AppDbContext in " + symbol, Assert.Single(report.PersistenceViolations));
    }

    [Fact]
    public void TopLevelMethodGroup_ResolvesAHandlerInAnotherFile()
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", """
            using Cluckwork.Temp.Handlers;
            app.MapGet("/account", Handlers.Read);
            """);
        WriteSource("Cluckwork.Temp.Api/Handlers.cs", """
            namespace Cluckwork.Temp.Handlers;
            public static class Handlers {
                public static void Read(Cluckwork.Temp.Farm.Account account, AppDbContext db) { }
                public static void Configure(DbContext db) { }
            }
            """);
        var report = Scan();
        Assert.Equal(1, report.WalkedAdapterCount);
        Assert.Equal("Cluckwork.Temp.Api.Program.MapGet(/account).Read", Assert.Single(report.LiveReach).Symbol);
        Assert.Contains("AppDbContext in Cluckwork.Temp.Api.Program.MapGet(/account).Read", Assert.Single(report.PersistenceViolations));
    }

    [Fact]
    public void TopLevelCompositionAndProgramsOutsideTheLedger_AreNotAdapters()
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", """
            services.GetRequiredService<AppDbContext>();
            app.MapGroup("/x");
            void Configure(Cluckwork.Temp.Farm.Account account, DbContext db) { }
            """);
        WriteSource("Cluckwork.Temp.Other/Program.cs", "app.MapGet(\"/x\", (AppDbContext db) => db);");
        var report = Scan();
        Assert.Equal(0, report.WalkedAdapterCount);
        Assert.Empty(AdapterReachScanner.Evaluate(report));
    }

    [Fact]
    public void TopLevelPartialProgramMethodGroup_IsResolved()
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", """
            app.MapGet("/x", Handler);
            public partial class Program {
                static void Handler(Cluckwork.Temp.Farm.Account account, AppDbContext db) { }
            }
            """);
        var report = Scan();
        Assert.Empty(report.RouteErrors);
        Assert.Equal(1, report.TopLevelProgramAdapterCount);
        Assert.Equal("Cluckwork.Temp.Api.Program.MapGet(/x).Handler", Assert.Single(report.LiveReach).Symbol);
        Assert.Single(report.PersistenceViolations);
    }

    [Theory]
    [InlineData("app.MapGet(\"/x\", MissingHandler);")]
    [InlineData("app.MapGet(routeFromConfig, (AppDbContext db) => db);")]
    public void UnresolvedTopLevelHandlerOrRoute_FailsTheWalk(string mapping)
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", mapping);
        var report = Scan();
        Assert.Single(report.RouteErrors);
        Assert.Contains(AdapterReachScanner.Evaluate(report), failure => failure.Contains("the walk cannot be trusted", StringComparison.Ordinal));
    }

    [Fact]
    public void TopLevelRouteWithNoReach_StillCountsAndNeedsNoRow()
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", "app.MapGet(\"/x\", context => context);");
        var report = Scan();
        Assert.Equal(1, report.WalkedAdapterCount);
        Assert.Equal(1, report.TopLevelProgramAdapterCount);
        Assert.Empty(report.LiveReach);
        Assert.Empty(AdapterReachScanner.Evaluate(report));
    }

    [Theory]
    [InlineData("(Func<AppDbContext, object>)((AppDbContext db) => db)", "")]
    [InlineData("Handler<int>", "static object Handler<T>(AppDbContext db) => db;")]
    public void TopLevelCastedLambdaAndGenericMethodGroup_AreAdapters(string handler, string declaration)
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", $$"""
            app.MapGet("/x", {{handler}});
            {{declaration}}
            """);
        var report = Scan();
        Assert.Empty(report.ParseErrors);
        Assert.Empty(report.RouteErrors);
        Assert.Equal(1, report.TopLevelProgramAdapterCount);
        Assert.Contains("AppDbContext in Cluckwork.Temp.Api.Program.MapGet(/x)", Assert.Single(report.PersistenceViolations));
    }

    [Fact]
    public void TopLevelSamePathUnderDifferentVerbs_HasIndependentReachAllowances()
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", """
            app.MapGet("/api/v1/x", (Cluckwork.Temp.Farm.Account account) => account);
            app.MapPost("/api/v1/x", (Cluckwork.Temp.Farm.Account account) => account);
            """);
        const string get = "Cluckwork.Temp.Api.Program.MapGet(/api/v1/x)";
        const string post = "Cluckwork.Temp.Api.Program.MapPost(/api/v1/x)";
        var report = Scan(Row(symbol: get));
        Assert.Equal([get, post], report.LiveReach.Select(r => r.Symbol));
        Assert.Equal(post, Assert.Single(report.Undeclared).Symbol);
        Assert.Contains(post + " -> Farm", Assert.Single(AdapterReachScanner.Evaluate(report)));
    }

    [Theory]
    [InlineData("new[] { \"GET\" }", "new[] { \"POST\" }")]
    [InlineData("new string[] { \"GET\" }", "new string[] { \"POST\" }")]
    [InlineData("[\"GET\"]", "[\"POST\"]")]
    [InlineData("new List<string> { \"GET\" }", "new List<string> { \"POST\" }")]
    public void TopLevelMapMethodsLiteralLists_HaveIndependentReachAllowances(string getMethods, string postMethods)
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", $$"""
            app.MapMethods("/x", {{getMethods}}, (Cluckwork.Temp.Farm.Account account) => account);
            app.MapMethods("/x", {{postMethods}}, (Cluckwork.Temp.Farm.Account account) => account);
            """);
        const string get = "Cluckwork.Temp.Api.Program.MapMethods(/x;[GET])";
        const string post = "Cluckwork.Temp.Api.Program.MapMethods(/x;[POST])";
        var report = Scan(Row(symbol: get));
        Assert.Equal([get, post], report.LiveReach.Select(r => r.Symbol));
        Assert.Equal(post, Assert.Single(report.Undeclared).Symbol);
        Assert.Contains(post + " -> Farm", Assert.Single(AdapterReachScanner.Evaluate(report)));
    }

    [Fact]
    public void TopLevelSameRouteWithDifferentMethodGroups_KeepsTheHandlerName()
    {
        WriteSource("Cluckwork.Temp.Api/Program.cs", """
            app.MapGet("/x", Read);
            app.MapGet("/x", Write);
            object Read(Cluckwork.Temp.Farm.Account account) => account;
            object Write(Cluckwork.Temp.Farm.Account account) => account;
            """);
        const string read = "Cluckwork.Temp.Api.Program.MapGet(/x).Read";
        const string write = "Cluckwork.Temp.Api.Program.MapGet(/x).Write";
        var report = Scan(Row(symbol: read));
        Assert.Equal([read, write], report.LiveReach.Select(r => r.Symbol));
        Assert.Equal(write, Assert.Single(report.Undeclared).Symbol);
        Assert.Contains(write + " -> Farm", Assert.Single(AdapterReachScanner.Evaluate(report)));
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
