namespace Cluckwork.Application.Tests.Architecture;

// #842 — ledger semantics on a temp tree, one named assertion per failure class.

public sealed class ModuleLedgerTests : IDisposable
{
    private const string Owners = """
          "owners": {
            "Red":  { "kind": "module",   "namespaces": ["Cluckwork.Temp.Red"] },
            "Blue": { "kind": "module",   "namespaces": ["Cluckwork.Temp.Blue"] },
            "Hub":  { "kind": "platform", "namespaces": ["Cluckwork.Temp.Hub"] }
          }
        """;

    private const string BlueSource = """
        namespace Cluckwork.Temp.Blue;
        public class B { public static string Name => "b"; }
        """;

    private readonly string _tempRoot = Directory.CreateTempSubdirectory("module-ledger-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    private void WriteSource(string relativePath, string content)
    {
        var full = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string WriteLedger(string edges, string owners = Owners)
    {
        var path = Path.Combine(_tempRoot, "module-ledger.json");
        File.WriteAllText(path, "{\n" + owners + ",\n  \"edges\": [" + edges + "]\n}\n");
        return path;
    }

    private static string Cell(string from, string to, params string[] symbols)
    {
        var quoted = string.Join(", ", symbols.Select(s => "\"" + s + "\""));
        return $"{{ \"from\": \"{from}\", \"to\": \"{to}\", \"kind\": \"R\", \"reason\": \"fixture\", \"symbols\": [{quoted}] }}";
    }

    private IReadOnlyList<string> Evaluate(string ledgerPath) =>
        ModuleLedgerScanner.Evaluate(ModuleLedgerScanner.Scan(Path.Combine(_tempRoot, "src"), ledgerPath));

    private ModuleLedgerReport Scan(string ledgerPath) =>
        ModuleLedgerScanner.Scan(Path.Combine(_tempRoot, "src"), ledgerPath);

    [Fact]
    public void UsingDirective_CrossingOwners_IsAnUndeclaredEdge()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            using Cluckwork.Temp.Blue;
            public class R { public string Go() => B.Name; }
            """);

        var failure = Evaluate(WriteLedger(string.Empty))
            .FirstOrDefault(f => f.Contains("undeclared cross-owner edge"));

        Assert.False(string.IsNullOrEmpty(failure), "expected an undeclared-edge failure");
        Assert.Contains("Red -> Blue", failure!);
        Assert.Contains("Cluckwork.Temp.Red.R", failure!);
        Assert.Contains("Cluckwork.Temp.Blue", failure!);
    }

    [Fact]
    public void InlineQualifiedName_WithNoUsing_IsAnUndeclaredEdge()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            public class R { public string Go() => Cluckwork.Temp.Blue.B.Name; }
            """);

        var failure = Evaluate(WriteLedger(string.Empty))
            .FirstOrDefault(f => f.Contains("undeclared cross-owner edge"));

        Assert.False(string.IsNullOrEmpty(failure), "expected an undeclared-edge failure");
        Assert.Contains("Red -> Blue", failure!);
        Assert.Contains("Cluckwork.Temp.Red.R", failure!);
    }

    [Fact]
    public void DeclaredEdge_IsGreen()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            using Cluckwork.Temp.Blue;
            public class R { public string Go() => B.Name; }
            """);

        var failures = Evaluate(WriteLedger(Cell("Red", "Blue", "Cluckwork.Temp.Red.R")));

        Assert.True(failures.Count == 0, "expected a green gate, got: " + string.Join(" | ", failures));
    }

    [Fact]
    public void SymbolNoReferenceRealises_IsAStaleRow()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            using Cluckwork.Temp.Blue;
            public class R { public string Go() => B.Name; }
            """);

        var failure = Evaluate(WriteLedger(
                Cell("Red", "Blue", "Cluckwork.Temp.Red.R", "Cluckwork.Temp.Red.Gone")))
            .FirstOrDefault(f => f.Contains("stale ledger row"));

        Assert.False(string.IsNullOrEmpty(failure), "expected a stale-row failure");
        Assert.Contains("Cluckwork.Temp.Red.Gone", failure!);
    }

    [Fact]
    public void CellWithNoSymbols_IsAStaleRow()
    {
        WriteSource("src/Blue.cs", BlueSource);

        var failure = Evaluate(WriteLedger(Cell("Red", "Blue")))
            .FirstOrDefault(f => f.Contains("stale ledger row"));

        Assert.False(string.IsNullOrEmpty(failure), "expected a stale-row failure for an empty cell");
        Assert.Contains("Red -> Blue", failure!);
    }

    [Fact]
    public void DeclaredNamespaceNoOwnerClaims_IsUnowned()
    {
        WriteSource("src/Green.cs", """
            namespace Cluckwork.Temp.Green;
            public class G { }
            """);

        var failure = Evaluate(WriteLedger(string.Empty))
            .FirstOrDefault(f => f.Contains("unowned namespace"));

        Assert.False(string.IsNullOrEmpty(failure), "expected an unowned-namespace failure");
        Assert.Contains("Cluckwork.Temp.Green", failure!);
        Assert.Contains("src/Green.cs", failure!);
    }

    [Fact]
    public void ReferencedNamespaceNoOwnerClaims_IsUnowned()
    {
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            using Cluckwork.Temp.Green;
            public class R { }
            """);

        var failure = Evaluate(WriteLedger(string.Empty))
            .FirstOrDefault(f => f.Contains("referenced from"));

        Assert.False(string.IsNullOrEmpty(failure), "expected an unowned referenced-namespace failure");
        Assert.Contains("Cluckwork.Temp.Green", failure!);
        Assert.Contains("src/Red.cs", failure!);
    }

    [Fact]
    public void FileLevelUsing_AttributesToEveryTopLevelType()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            using Cluckwork.Temp.Blue;
            public class First { }
            public class Second { }
            """);

        var report = Scan(WriteLedger(string.Empty));

        Assert.Equal(
            ["Cluckwork.Temp.Red.First", "Cluckwork.Temp.Red.Second"],
            report.LiveEdges.Select(e => e.Symbol));
    }

    [Fact]
    public void UsingInsideANamespaceBlock_ScopesToThatBlockOnly()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red
            {
                using Cluckwork.Temp.Blue;
                public class Inside { }
            }
            namespace Cluckwork.Temp.Red
            {
                public class Sibling { }
            }
            """);

        var report = Scan(WriteLedger(string.Empty));

        Assert.Equal("Cluckwork.Temp.Red.Inside", Assert.Single(report.LiveEdges).Symbol);
    }

    [Fact]
    public void NestedType_RollsUpToItsTopLevelType()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            public class Outer
            {
                public class Inner { public string Go() => Cluckwork.Temp.Blue.B.Name; }
            }
            """);

        var report = Scan(WriteLedger(string.Empty));

        Assert.Equal("Cluckwork.Temp.Red.Outer", Assert.Single(report.LiveEdges).Symbol);
    }

    [Fact]
    public void PlatformOnEitherEnd_IsNeverAnEdge()
    {
        WriteSource("src/Hub.cs", """
            namespace Cluckwork.Temp.Hub;
            using Cluckwork.Temp.Red;
            public class H { }
            """);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            using Cluckwork.Temp.Hub;
            public class R { }
            """);

        Assert.Empty(Scan(WriteLedger(string.Empty)).LiveEdges);

        const string hubAsModule = """
              "owners": {
                "Red": { "kind": "module", "namespaces": ["Cluckwork.Temp.Red"] },
                "Hub": { "kind": "module", "namespaces": ["Cluckwork.Temp.Hub"] }
              }
            """;
        Assert.Equal(
            [("Hub", "Red"), ("Red", "Hub")],
            Scan(WriteLedger(string.Empty, hubAsModule)).LiveEdges.Select(e => (e.From, e.To)));
    }

    [Fact]
    public void ParseError_MakesTheWalkUntrusted()
    {
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            public class R { public void Broken( }
            """);

        var failure = Evaluate(WriteLedger(string.Empty))
            .FirstOrDefault(f => f.Contains("the walk cannot be trusted"));

        Assert.False(string.IsNullOrEmpty(failure), "expected a parse-error failure");
        Assert.Contains("src/Red.cs", failure!);
    }

    [Fact]
    public void LongestNamespacePrefixWins_OverASiblingClaimingTheShorterOne()
    {
        const string owners = """
              "owners": {
                "Wide": { "kind": "module", "namespaces": ["Cluckwork.Temp"] },
                "Blue": { "kind": "module", "namespaces": ["Cluckwork.Temp.Blue"] },
                "Red":  { "kind": "module", "namespaces": ["Cluckwork.Temp.Red"] }
              }
            """;
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            public class R { public string Go() => Cluckwork.Temp.Blue.B.Name; }
            """);

        var report = Scan(WriteLedger(string.Empty, owners));

        Assert.Equal("Blue", Assert.Single(report.LiveEdges).To);
    }

    [Fact]
    public void ExactClaim_CoversTheNamespaceButNotItsChildren()
    {
        const string owners = """
              "owners": {
                "Hub": { "kind": "platform", "namespaces": ["Cluckwork.Temp.Hub"], "exactNamespaces": ["Cluckwork.Temp"] },
                "Red": { "kind": "module",   "namespaces": ["Cluckwork.Temp.Red"] }
              }
            """;
        WriteSource("src/Root.cs", "namespace Cluckwork.Temp;\npublic class Root { }\n");
        WriteSource("src/Child.cs", "namespace Cluckwork.Temp.Child;\npublic class C { }\n");

        var failures = Evaluate(WriteLedger(string.Empty, owners));

        var unowned = Assert.Single(failures, f => f.Contains("unowned namespace"));
        Assert.Contains("'Cluckwork.Temp.Child'", unowned);
    }

    [Fact]
    public void GlobalAliasQualifiedName_IsAnEdge()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            public class R { public global::Cluckwork.Temp.Blue.B? Held; }
            """);

        var edge = Assert.Single(Scan(WriteLedger(string.Empty)).LiveEdges);

        Assert.Equal(("Red", "Blue", "Cluckwork.Temp.Red.R"), (edge.From, edge.To, edge.Symbol));
    }

    [Fact]
    public void CellWithOneOwnerOnBothEnds_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(WriteLedger(Cell("Red", "Red", "Cluckwork.Temp.Red.R")));

        Assert.Contains("names one owner on both ends", failure);
    }

    [Fact]
    public void TempTreeFloor_IsItsOwnFileCount()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");
        WriteSource("src/Hub.cs", "namespace Cluckwork.Temp.Hub;\npublic class H { }\n");

        var report = Scan(WriteLedger(string.Empty));

        Assert.Equal(3, report.ScannedFileCount);
        Assert.Equal(3, report.ExpectedFileCountFloor);
        Assert.DoesNotContain(ModuleLedgerScanner.Evaluate(report), f => f.Contains("the walk saw less than it should"));
    }

    [Fact]
    public void DuplicateOwnerName_IsARegistryError()
    {
        const string owners = """
              "owners": {
                "Red":  { "kind": "module", "namespaces": ["Cluckwork.Temp.Red"] },
                "Red":  { "kind": "module", "namespaces": ["Cluckwork.Temp.Blue"] }
              }
            """;
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(WriteLedger(string.Empty, owners));

        Assert.Contains("duplicate owner 'Red'", failure);
    }

    [Fact]
    public void NamespaceClaimedByTwoOwners_IsARegistryError()
    {
        const string owners = """
              "owners": {
                "Red":  { "kind": "module", "namespaces": ["Cluckwork.Temp.Red"] },
                "Blue": { "kind": "module", "namespaces": ["Cluckwork.Temp.Red"] }
              }
            """;
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(WriteLedger(string.Empty, owners));

        Assert.Contains("namespace 'Cluckwork.Temp.Red' is claimed 2 times", failure);
    }

    [Fact]
    public void CellNamingAnUnknownOwner_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(WriteLedger(Cell("Red", "Purple", "Cluckwork.Temp.Red.R")));

        Assert.Contains("references unknown owner 'Purple'", failure);
    }

    [Fact]
    public void TwoCellsForTheSamePair_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(WriteLedger(
            Cell("Red", "Blue", "Cluckwork.Temp.Red.R") + "," + Cell("Red", "Blue", "Cluckwork.Temp.Red.R")));

        Assert.Contains("duplicate edge cell Red -> Blue", failure);
    }

    [Fact]
    public void SymbolListedTwiceInACell_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(WriteLedger(
            Cell("Red", "Blue", "Cluckwork.Temp.Red.R", "Cluckwork.Temp.Red.R")));

        Assert.Contains("lists symbol 'Cluckwork.Temp.Red.R' 2 times", failure);
    }

    [Fact]
    public void CellTouchingAPlatformOwner_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(WriteLedger(Cell("Red", "Hub", "Cluckwork.Temp.Red.R")));

        Assert.Contains("names platform owner 'Hub'", failure);
    }

    [Fact]
    public void MalformedCell_IsARegistryErrorRatherThanADroppedRow()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(WriteLedger(
            """{ "from": "Red", "to": "Blue", "kind": "X", "reason": "  ", "symbols": ["Cluckwork.Temp.Red.R"] }"""));

        Assert.Contains("has kind 'X'", failure);
        Assert.Contains("blank reason", failure);
    }

    private string RegistryFailure(string ledgerPath)
    {
        var failure = Evaluate(ledgerPath).FirstOrDefault(f => f.Contains("registry error"));
        Assert.False(string.IsNullOrEmpty(failure), "expected a registry-error failure");
        return failure!;
    }
}
