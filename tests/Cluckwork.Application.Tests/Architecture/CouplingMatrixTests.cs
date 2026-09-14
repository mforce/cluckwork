namespace Cluckwork.Application.Tests.Architecture;

public sealed class CouplingMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coupling-matrix-" + Guid.NewGuid());

    [Fact]
    public void Render_UsesLiveEdgesForeignKeysAndAdapterReaches()
    {
        var sourceRoot = Path.Combine(_root, "src");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "Alpha.cs"), """
            using Cluckwork.Beta;
            namespace Cluckwork.Alpha;
            public sealed class Writer { private BetaType Value { get; } = new(); }
            public sealed class Reader { private BetaType Value { get; } = new(); }
            """);
        File.WriteAllText(Path.Combine(sourceRoot, "Beta.cs"), """
            namespace Cluckwork.Beta;
            public sealed class BetaType { }
            """);
        var ledgerPath = Path.Combine(_root, "module-ledger.json");
        File.WriteAllText(ledgerPath, """
            {
              "owners": {
                "Alpha": { "kind": "module", "namespaces": ["Cluckwork.Alpha"] },
                "Beta": { "kind": "module", "namespaces": ["Cluckwork.Beta"] },
                "Platform": { "kind": "platform", "namespaces": ["Cluckwork.Platform"] }
              },
              "edges": [
                {
                  "from": "Alpha", "to": "Beta", "kind": "W", "reason": "test",
                  "symbols": ["Cluckwork.Alpha.Writer", "Cluckwork.Alpha.Reader"]
                }
              ]
            }
            """);
        var ledger = ModuleLedger.Load(ledgerPath);
        var edgeReport = ModuleLedgerScanner.Scan(sourceRoot, ledgerPath);
        var tableReport = new TableOwnerReport(2,
            [new CrossOwnerForeignKey("BetaRows", "FK_BetaRows_AlphaRows_AlphaId", "Alpha", "Beta")], [], []);
        var adapterReport = new AdapterReachReport(
        [
            new AdapterReach("Platform.Endpoint.One", "Alpha", "Cluckwork.Alpha.Reader", "Endpoint.cs", 1),
            new AdapterReach("Platform.Endpoint.Two", "Alpha", "Cluckwork.Alpha.Writer", "Endpoint.cs", 2),
            new AdapterReach("Platform.Endpoint.Two", "Beta", "Cluckwork.Beta.Reader", "Endpoint.cs", 2),
        ], [], [], [], [], [], [], 2, 2);

        var rendered = CouplingMatrix.Render(ledger, edgeReport, tableReport, adapterReport);

        Assert.Contains("| Alpha | — | W (2) fk:1 | P |", rendered);
        Assert.Contains("| Beta | — | — | P |", rendered);
        Assert.Contains("| Platform | A (2) | A (1) | — |", rendered);
        Assert.Contains("| FK_BetaRows_AlphaRows_AlphaId | BetaRows | Alpha | Beta |", rendered);
    }

    [Fact]
    public void Render_ReportsAHandWrittenKindChange()
    {
        var sourceRoot = Path.Combine(_root, "kind-change-src");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "Access.cs"), """
            using Cluckwork.Farm;
            namespace Cluckwork.Access;
            public sealed class Writer { private Account Value { get; } = new(); }
            """);
        File.WriteAllText(Path.Combine(sourceRoot, "Farm.cs"), """
            namespace Cluckwork.Farm;
            public sealed class Account { }
            """);
        var ledgerPath = Path.Combine(_root, "kind-change-ledger.json");
        File.WriteAllText(ledgerPath, """
            {
              "owners": {
                "Access": { "kind": "module", "namespaces": ["Cluckwork.Access"] },
                "Farm": { "kind": "module", "namespaces": ["Cluckwork.Farm"] },
                "Platform": { "kind": "platform", "namespaces": ["Cluckwork.Platform"] }
              },
              "edges": [
                {
                  "from": "Access", "to": "Farm", "kind": "W", "reason": "test",
                  "symbols": ["Cluckwork.Access.Writer"]
                }
              ]
            }
            """);
        var ledger = ModuleLedger.Load(ledgerPath);
        var edgeReport = ModuleLedgerScanner.Scan(sourceRoot, ledgerPath);
        var emptyTables = new TableOwnerReport(0, [], [], []);
        var emptyAdapters = new AdapterReachReport([], [], [], [], [], [], [], 0, 0);

        var rendered = CouplingMatrix.Render(ledger, edgeReport, emptyTables, emptyAdapters);

        Assert.Contains("| Access | Farm | R | W (1) |", rendered);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
