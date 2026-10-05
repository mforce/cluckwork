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

    private static string Edge(string symbol) =>
        $"[ModuleEdge(\"Red\", \"Blue\", \"R\", \"Red reads Blue.\", \"{symbol}\")]";

    private static CSharpAnalyzerTest<ModuleEdgeAnalyzer, DefaultVerifier> Fixture(string edges, string source)
    {
        var attributes = File.ReadAllText(Path.Combine(
            GuardScanner.FindRepoRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("repo root not found"),
            "src/Cluckwork.Domain/Common/Architecture/ModuleMapAttributes.cs"));
        var map = $"""
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

        var test = new CSharpAnalyzerTest<ModuleEdgeAnalyzer, DefaultVerifier>
        {
            // The running runtime's assemblies, so the test never downloads a reference pack.
            ReferenceAssemblies = new ReferenceAssemblies("net10.0"),
            TestCode = source,
        };
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
