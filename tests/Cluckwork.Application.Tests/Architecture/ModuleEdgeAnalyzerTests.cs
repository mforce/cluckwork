using Cluckwork.Analyzers;
using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Cluckwork.Application.Tests.Architecture;

// #859: the analyzer over a two-project fixture. The fixture's Cluckwork.Domain compiles the real
// ModuleMapAttributes.cs with a map of Red, Blue and Platform; the code under test is a second project referencing it.
public sealed class ModuleEdgeAnalyzerTests
{
    private const string BlueSource = """
        namespace Cluckwork.Domain.Blue;
        public sealed class B { public string Name => ""; }
        """;

    private const string HubSource = """
        namespace Cluckwork.Domain.Common;
        public sealed class Hub { public Cluckwork.Domain.Blue.B Blue { get; } = new(); }
        """;

    [Fact]
    public Task UndeclaredEdge_IsCW1001() =>
        Fixture(edges: "", """
            namespace Cluckwork.Application.Red;
            public sealed class R { public object Go() => {|CW1001:new Cluckwork.Domain.Blue.B()|}; }
            """).RunAsync();

    [Fact]
    public Task DeclaredEdge_IsClean() =>
        Fixture(Edge("Cluckwork.Application.Red.R"), """
            namespace Cluckwork.Application.Red;
            public sealed class R { public object Go() => new Cluckwork.Domain.Blue.B(); }
            """).RunAsync();

    // The type is never named; binding Name still charges its declaring type (#1071).
    [Fact]
    public Task MemberAccessOnlyReference_IsAnEdge() =>
        Fixture(edges: "", """
            namespace Cluckwork.Application.Red;
            public sealed class R { public string Go(Cluckwork.Domain.Common.Hub hub) => {|CW1001:hub.Blue.Name|}; }
            """).RunAsync();

    [Fact]
    public Task StaleRow_IsCW1002()
    {
        var test = Fixture(Edge("Cluckwork.Application.Red.R"), """
            namespace Cluckwork.Application.Red;
            public sealed class R;
            """);
        test.ExpectedDiagnostics.Add(new DiagnosticResult("CW1002", DiagnosticSeverity.Error).WithArguments(
            "Red", "Blue", "Cluckwork.Application.Red.R",
            "no reference in src/ realises this edge; delete the symbol from its ModuleEdge row in src/Cluckwork.Domain/Common/Architecture/Modules/Red.cs, or restore the dependency it was written for"));
        return test.RunAsync();
    }

    [Fact]
    public Task UnownedNamespace_IsCW1003() =>
        Fixture(edges: "", """
            namespace Cluckwork.Application.Green;
            public sealed class {|CW1003:G|};
            """).RunAsync();

    [Fact]
    public Task UsingWithNoUse_IsClean() =>
        Fixture(edges: "", """
            using Cluckwork.Domain.Blue;
            using static Cluckwork.Domain.Blue.B;
            namespace Cluckwork.Application.Red;
            public sealed class R;
            """).RunAsync();

    // #1116: CW1004. Blue publishes a contract with a public and an internal nested type, a public type nested in an
    // internal one, a private type, a seam type
    // and a port its contract interface inherits. Red is a peer module with a declared edge; Cluckwork.Api.Modules is an
    // adapter root.
    private const string ReachSource = """
        [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TestProject")]
        namespace Cluckwork.Domain.Modules.Blue.Contracts
        {
            public sealed class Pub { public sealed class Nested; internal sealed class Hidden; }
            public interface IBlueModule : Cluckwork.Domain.Modules.Blue.Inner.IBlueQueries;
            internal static class Root { public sealed class Child; }
        }
        namespace Cluckwork.Domain.Modules.Blue.Inner
        {
            public sealed class Priv;
            public sealed class Seamed;
            public interface IBlueQueries { int Count(); }
        }
        """;

    [Fact]
    public Task PeerBodyNamingAPrivateType_IsCW1004() => Reach("""
        namespace Cluckwork.Application.Modules.Red;
        public sealed class R { public object Go() => typeof({|CW1004:Cluckwork.Domain.Modules.Blue.Inner.Priv|}); }
        """).RunAsync();

    [Fact]
    public Task PeerNamingAContractOrSeamType_IsClean() => Reach("""
        namespace Cluckwork.Application.Modules.Red;
        public sealed class R
        {
            public object Go() => (typeof(Cluckwork.Domain.Modules.Blue.Contracts.Pub), typeof(Cluckwork.Domain.Modules.Blue.Inner.Seamed));
        }
        """).RunAsync();

    [Fact]
    public Task PublicNestedTypeOfAContract_IsContract() => Reach("""
        namespace Cluckwork.Api.Modules.Shop;
        public sealed class E { public object Go() => typeof(Cluckwork.Domain.Modules.Blue.Contracts.Pub.Nested); }
        """).RunAsync();

    [Fact]
    public Task InternalNestedTypeOfAContract_IsNotContract() => Reach("""
        namespace Cluckwork.Application.Modules.Red;
        public sealed class R { public object Go() => typeof({|CW1004:Cluckwork.Domain.Modules.Blue.Contracts.Pub.Hidden|}); }
        """).RunAsync();

    [Fact]
    public Task PublicNestedTypeOfAnInternalContractsType_IsNotContract() => Reach("""
        namespace Cluckwork.Application.Modules.Red;
        public sealed class R { public object Go() => typeof({|CW1004:Cluckwork.Domain.Modules.Blue.Contracts.Root.Child|}); }
        """).RunAsync();

    [Fact]
    public Task AdapterNamingTheSeam_IsCW1004() => Reach("""
        namespace Cluckwork.Api.Modules.Shop;
        public sealed class E { public object Go() => typeof({|CW1004:Cluckwork.Domain.Modules.Blue.Inner.Seamed|}); }
        """).RunAsync();

    [Fact]
    public Task AdapterCallThroughTheContractedReceiver_IsClean() => Reach("""
        namespace Cluckwork.Api.Modules.Shop;
        public sealed class E
        {
            public int Go(Cluckwork.Domain.Modules.Blue.Contracts.IBlueModule blue) =>
                blue.Count() + (blue?.Count() ?? 0) + (blue).Count() + (blue.Count()!) + ((blue.Count())!);
        }
        """).RunAsync();

    [Fact]
    public Task AdapterTakingTheInheritedPortDirectly_IsCW1004() => Reach("""
        namespace Cluckwork.Api.Modules.Shop;
        public sealed class E { public int Go({|CW1004:Cluckwork.Domain.Modules.Blue.Inner.IBlueQueries|} q) => q.Count(); }
        """).RunAsync();

    private static CSharpAnalyzerTest<ModuleEdgeAnalyzer, DefaultVerifier> Reach(string source)
    {
        var test = Fixture("", source, """
            [ModuleOwner("Red", "module", Namespaces = ["Cluckwork.Application.Modules.Red"])]
            [ModuleEdge("Red", "Blue", "R", "Red reads Blue.", "Cluckwork.Application.Modules.Red.R")]
            internal static class RedModuleRules;
            [ModuleOwner("Blue", "module", Namespaces = ["Cluckwork.Domain.Modules.Blue", "Cluckwork.Domain.Blue"], Seam = ["Cluckwork.Domain.Modules.Blue.Inner.Seamed"])]
            internal static class BlueModuleRules;
            [ModuleOwner("Platform", "platform", Namespaces = ["Cluckwork.Domain.Common", "Cluckwork.Api"], ExactNamespaces = ["Cluckwork.Domain", "Cluckwork.Application"])]
            [AdapterRoots(Namespaces = ["Cluckwork.Api.Modules"])]
            internal static class PlatformModuleRules;
            """, ReachSource);
        test.DisabledDiagnostics.Remove("CW1004");
        return test;
    }

    private static string Edge(string symbol) =>
        $"[ModuleEdge(\"Red\", \"Blue\", \"R\", \"Red reads Blue.\", \"{symbol}\")]";

    private static CSharpAnalyzerTest<ModuleEdgeAnalyzer, DefaultVerifier> Fixture(
        string edges, string source, string? owners = null, string? extra = null)
    {
        var attributes = File.ReadAllText(Path.Combine(
            GuardScanner.FindRepoRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("repo root not found"),
            "src/Cluckwork.Domain/Common/Architecture/ModuleMapAttributes.cs"));
        var map = owners is not null ? "using Cluckwork.Domain.Common.Architecture;\n" + owners : $"""
            using Cluckwork.Domain.Common.Architecture;
            [ModuleOwner("Red", "module", Namespaces = ["Cluckwork.Application.Red"])]
            {edges}
            internal static class RedModuleRules;
            [ModuleOwner("Blue", "module", Namespaces = ["Cluckwork.Domain.Blue"])]
            internal static class BlueModuleRules;
            [ModuleOwner("Platform", "platform", Namespaces = ["Cluckwork.Domain.Common"], ExactNamespaces = ["Cluckwork.Domain", "Cluckwork.Application"])]
            internal static class PlatformModuleRules;
            """;

        var domain = new ProjectState("Cluckwork.Domain", LanguageNames.CSharp, "/domain/", "cs");
        domain.Sources.Add(("/domain/ModuleMapAttributes.cs", "using System;\n" + attributes));
        domain.Sources.Add(("/domain/ModuleMap.cs", map));
        domain.Sources.Add(("/domain/B.cs", BlueSource));
        domain.Sources.Add(("/domain/Hub.cs", HubSource));
        if (extra is not null)
        {
            domain.Sources.Add(("/domain/Extra.cs", extra));
        }

        var test = new CSharpAnalyzerTest<ModuleEdgeAnalyzer, DefaultVerifier>
        {
            // The running runtime's assemblies, so the test never downloads a reference pack.
            ReferenceAssemblies = new ReferenceAssemblies("net10.0"),
            TestCode = source,
        };
        // Blue.B sits outside any Contracts namespace; the CW1004 cases run through Reach.
        test.DisabledDiagnostics.Add("CW1004");
        test.TestState.AdditionalProjects.Add("Cluckwork.Domain", domain);
        test.TestState.AdditionalProjectReferences.Add("Cluckwork.Domain");
        test.SolutionTransforms.Add((solution, _) => solution.Projects.Aggregate(solution, (s, project) =>
            s.AddMetadataReferences(project.Id, RuntimeReferences.Value)));
        return test;
    }

    private static readonly Lazy<MetadataReference[]> RuntimeReferences = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll")
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray());
}
