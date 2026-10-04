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
        var ledger = ModuleLedger.Validate(new ModuleLedger(
            [
                new("Alpha", "module", ["Cluckwork.Alpha"], []),
                new("Beta", "module", ["Cluckwork.Beta"], []),
                new("Platform", "platform", ["Cluckwork.Platform"], []),
            ],
            [new("Alpha", "Beta", "W", "test", ["Cluckwork.Alpha.Writer", "Cluckwork.Alpha.Reader"])], []));
        var edgeReport = ModuleLedgerScanner.Scan(sourceRoot, ledger);
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
        var ledger = ModuleLedger.Validate(new ModuleLedger(
            [
                new("Access", "module", ["Cluckwork.Access"], []),
                new("Farm", "module", ["Cluckwork.Farm"], []),
                new("Platform", "platform", ["Cluckwork.Platform"], []),
            ],
            [new("Access", "Farm", "W", "test", ["Cluckwork.Access.Writer"])], []));
        var edgeReport = ModuleLedgerScanner.Scan(sourceRoot, ledger);
        var emptyTables = new TableOwnerReport(0, [], [], []);
        var emptyAdapters = new AdapterReachReport([], [], [], [], [], [], [], 0, 0);

        var rendered = CouplingMatrix.Render(ledger, edgeReport, emptyTables, emptyAdapters);

        Assert.Contains("| Access | Farm | R | W (1) |", rendered);
    }

    [Fact]
    public void Render_ReportsAHandWrittenEventAsUnobservable()
    {
        var sourceRoot = Path.Combine(_root, "event-src");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "Access.cs"), """
            namespace Cluckwork.Access;
            public sealed class Writer { }
            """);
        File.WriteAllText(Path.Combine(sourceRoot, "Insights.cs"), """
            namespace Cluckwork.Insights;
            public sealed class Projection { }
            """);
        var ledger = ModuleLedger.Validate(new ModuleLedger(
            [
                new("Access", "module", ["Cluckwork.Access"], []),
                new("Insights", "module", ["Cluckwork.Insights"], []),
                new("Platform", "platform", ["Cluckwork.Platform"], []),
            ],
            [], []));
        var edgeReport = ModuleLedgerScanner.Scan(sourceRoot, ledger);
        var emptyTables = new TableOwnerReport(0, [], [], []) { ExpectedTableCountFloor = 0 };
        var emptyAdapters = new AdapterReachReport([], [], [], [], [], [], [], 0, 0);

        var rendered = CouplingMatrix.Render(ledger, edgeReport, emptyTables, emptyAdapters);

        Assert.Contains("## Cells the generator cannot observe", rendered);
        Assert.Contains("| Access | Insights | E | — |", rendered);
    }

    [Fact]
    public void CommittedMatrixWithCrLfEndings_MatchesRegeneration()
    {
        CouplingMatrixRealTreeTests.AssertCommittedMatrixMatches("first\r\nsecond\r\n", "first\nsecond\n");
    }

    [Fact]
    public void RenderChecked_RefusesAStaleForeignKeyRow()
    {
        var ledger = new ModuleLedger([], [], [])
        {
            ForeignKeys = [new ForeignKeyCell("Missing", "FK_Missing", "Access", "Farm", "test")],
        };
        var edges = new ModuleLedgerReport([], [], [], [], [], [], [], 0, 0);
        var tables = new TableOwnerReport(0, [], [], ["stale foreign-key row 'FK_Missing'"])
        {
            ExpectedTableCountFloor = 0,
        };
        var adapters = new AdapterReachReport([], [], [], [], [], [], [], 0, 0);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CouplingMatrixRealTreeTests.RenderChecked(ledger, edges, tables, adapters));

        Assert.Contains("stale foreign-key row 'FK_Missing'", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
