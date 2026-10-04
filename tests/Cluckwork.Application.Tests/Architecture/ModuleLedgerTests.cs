namespace Cluckwork.Application.Tests.Architecture;

// #842 — ledger semantics on a temp tree, one named assertion per failure class.

public sealed class ModuleLedgerTests : IDisposable
{
    private static readonly OwnerDefinition[] Owners =
    [
        Owner("Red", "module", "Cluckwork.Temp.Red"),
        Owner("Blue", "module", "Cluckwork.Temp.Blue"),
        Owner("Hub", "platform", "Cluckwork.Temp.Hub"),
    ];

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

    private static ModuleLedger Ledger(IReadOnlyList<EdgeCell> edges, IReadOnlyList<OwnerDefinition>? owners = null) =>
        ModuleLedger.Validate(new ModuleLedger(owners ?? Owners, edges, []));

    private static OwnerDefinition Owner(string name, string kind, string ns, string? exact = null) =>
        new(name, kind, [ns], exact is null ? [] : [exact]);

    private static EdgeCell Cell(string from, string to, params string[] symbols) =>
        new(from, to, "R", "fixture", symbols);

    private IReadOnlyList<string> Evaluate(ModuleLedger ledger) =>
        ModuleLedgerScanner.Evaluate(ModuleLedgerScanner.Scan(Path.Combine(_tempRoot, "src"), ledger));

    private ModuleLedgerReport Scan(ModuleLedger ledger) =>
        ModuleLedgerScanner.Scan(Path.Combine(_tempRoot, "src"), ledger);

    [Fact]
    public void UsingDirective_CrossingOwners_IsAnUndeclaredEdge()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            using Cluckwork.Temp.Blue;
            public class R { public string Go() => B.Name; }
            """);

        var failure = Evaluate(Ledger([]))
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

        var failure = Evaluate(Ledger([]))
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

        var failures = Evaluate(Ledger([Cell("Red", "Blue", "Cluckwork.Temp.Red.R")]));

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

        var failure = Evaluate(Ledger([
                Cell("Red", "Blue", "Cluckwork.Temp.Red.R", "Cluckwork.Temp.Red.Gone")]))
            .FirstOrDefault(f => f.Contains("stale ledger row"));

        Assert.False(string.IsNullOrEmpty(failure), "expected a stale-row failure");
        Assert.Contains("Cluckwork.Temp.Red.Gone", failure!);
    }

    [Fact]
    public void CellWithNoSymbols_IsAStaleRow()
    {
        WriteSource("src/Blue.cs", BlueSource);

        var failure = Evaluate(Ledger([Cell("Red", "Blue")]))
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

        var failure = Evaluate(Ledger([]))
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

        var failure = Evaluate(Ledger([]))
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

        var report = Scan(Ledger([]));

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

        var report = Scan(Ledger([]));

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

        var report = Scan(Ledger([]));

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

        Assert.Empty(Scan(Ledger([])).LiveEdges);

        OwnerDefinition[] hubAsModule =
        [
            Owner("Red", "module", "Cluckwork.Temp.Red"),
            Owner("Hub", "module", "Cluckwork.Temp.Hub"),
        ];
        Assert.Equal(
            [("Hub", "Red"), ("Red", "Hub")],
            Scan(Ledger([], hubAsModule)).LiveEdges.Select(e => (e.From, e.To)));
    }

    [Fact]
    public void ParseError_MakesTheWalkUntrusted()
    {
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            public class R { public void Broken( }
            """);

        var failure = Evaluate(Ledger([]))
            .FirstOrDefault(f => f.Contains("the walk cannot be trusted"));

        Assert.False(string.IsNullOrEmpty(failure), "expected a parse-error failure");
        Assert.Contains("src/Red.cs", failure!);
    }

    [Fact]
    public void LongestNamespacePrefixWins_OverASiblingClaimingTheShorterOne()
    {
        OwnerDefinition[] owners =
        [
            Owner("Wide", "module", "Cluckwork.Temp"),
            Owner("Blue", "module", "Cluckwork.Temp.Blue"),
            Owner("Red", "module", "Cluckwork.Temp.Red"),
        ];
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            public class R { public string Go() => Cluckwork.Temp.Blue.B.Name; }
            """);

        var report = Scan(Ledger([], owners));

        Assert.Equal("Blue", Assert.Single(report.LiveEdges).To);
    }

    [Fact]
    public void ExactClaim_CoversTheNamespaceButNotItsChildren()
    {
        OwnerDefinition[] owners =
        [
            Owner("Hub", "platform", "Cluckwork.Temp.Hub", "Cluckwork.Temp"),
            Owner("Red", "module", "Cluckwork.Temp.Red"),
        ];
        WriteSource("src/Root.cs", "namespace Cluckwork.Temp;\npublic class Root { }\n");
        WriteSource("src/Child.cs", "namespace Cluckwork.Temp.Child;\npublic class C { }\n");

        var failures = Evaluate(Ledger([], owners));

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

        var edge = Assert.Single(Scan(Ledger([])).LiveEdges);

        Assert.Equal(("Red", "Blue", "Cluckwork.Temp.Red.R"), (edge.From, edge.To, edge.Symbol));
    }

    [Fact]
    public void RelativeQualifiedName_IsAnEdge()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red; public record R(Temp.Blue.B Value);");

        var report = Scan(Ledger([]));
        Assert.True(report.LiveEdges.Count == 1, string.Join(" | ", report.UnownedNamespaces));
        var edge = report.LiveEdges[0];
        Assert.Equal(("Red", "Blue"), (edge.From, edge.To));
    }

    [Fact]
    public void CompoundUsingAlias_IsAnEdge()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", "using Bs = System.Collections.Generic.List<Cluckwork.Temp.Blue.B>; namespace Cluckwork.Temp.Red; public class R { }");

        var edge = Assert.Single(Scan(Ledger([])).LiveEdges);
        Assert.Equal(("Red", "Blue"), (edge.From, edge.To));
    }

    [Fact]
    public void GlobalModuleImport_IsAFailure_ButPlatformImportIsGreen()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Hub.cs", "global using Cluckwork.Temp.Blue; namespace Cluckwork.Temp.Hub; public class H { }");

        var failure = Assert.Single(Evaluate(Ledger([])));
        Assert.Contains("global using of module namespace 'Cluckwork.Temp.Blue'", failure);

        WriteSource("src/Hub.cs", "global using Cluckwork.Temp.Hub; namespace Cluckwork.Temp.Hub; public class H { }");
        Assert.Empty(Evaluate(Ledger([])));
    }

    [Fact]
    public void GlobalImportOfAPlatformNamespaceHoldingAClaimedType_IsAFailure()
    {
        WriteSource("src/Hub.cs", "global using Cluckwork.Temp.Hub; namespace Cluckwork.Temp.Hub; public class H { }");
        OwnerDefinition[] claimed = [Owners[0] with { Types = ["Cluckwork.Temp.Hub.H"] }, Owners[1], Owners[2]];

        var failure = Assert.Single(Evaluate(Ledger([], claimed)));
        Assert.Contains("global using of module namespace 'Cluckwork.Temp.Hub' in src/Hub.cs:1", failure);
    }

    [Fact]
    public void GenericTopLevelTypes_HaveDistinctSymbols()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", "using Cluckwork.Temp.Blue; namespace Cluckwork.Temp.Red; public class R { } public class R<T> { }");

        Assert.Equal(["Cluckwork.Temp.Red.R", "Cluckwork.Temp.Red.R<>"], Scan(Ledger([])).LiveEdges.Select(edge => edge.Symbol));
    }

    [Fact]
    public void NamespaceDeclarationNames_AreNotReferences()
    {
        WriteSource("src/Both.cs", "namespace Cluckwork.Temp.Red { public class R { } } namespace Cluckwork.Temp.Blue { public class B { } }");

        Assert.Empty(Scan(Ledger([])).LiveEdges);
    }

    [Fact]
    public void ExactClaim_ResolvesAReferencedTypeBelowItsNamespace()
    {
        OwnerDefinition[] owners =
        [
            Owner("Hub", "platform", "Cluckwork.Temp.Hub", "Cluckwork.Temp"),
            Owner("Red", "module", "Cluckwork.Temp.Red"),
        ];
        WriteSource("src/Root.cs", "namespace Cluckwork.Temp; public class Root { }");
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red; public class R { Cluckwork.Temp.Root? Value; }");

        Assert.Empty(Evaluate(Ledger([], owners)));
    }

    [Fact]
    public void Net10PreprocessorSymbol_EnablesAUsingEdge()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", "#if NET10_0\nusing Cluckwork.Temp.Blue;\n#endif\nnamespace Cluckwork.Temp.Red; public class R { }");

        var edge = Assert.Single(Scan(Ledger([])).LiveEdges);
        Assert.Equal(("Red", "Blue"), (edge.From, edge.To));
    }

    [Fact]
    public void CellWithOneOwnerOnBothEnds_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(Ledger([Cell("Red", "Red", "Cluckwork.Temp.Red.R")]));

        Assert.Contains("names one owner on both ends", failure);
    }

    [Fact]
    public void FileLocalTypes_WithTheSameName_HaveDistinctSymbols()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/One.cs", """
            namespace Cluckwork.Temp.Red;
            using Cluckwork.Temp.Blue;
            file class Probe { }
            """);
        WriteSource("src/Two.cs", """
            namespace Cluckwork.Temp.Red;
            using Cluckwork.Temp.Blue;
            file class Probe { }
            """);

        Assert.Equal(
            ["Cluckwork.Temp.Red.Probe@src/One.cs", "Cluckwork.Temp.Red.Probe@src/Two.cs"],
            Scan(Ledger([])).LiveEdges.Select(edge => edge.Symbol));
    }

    [Fact]
    public void DebugPreprocessorSymbol_EnablesAUsingEdgeOnlyInDebugBuilds()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red;
            #if DEBUG
            using Cluckwork.Temp.Blue;
            #endif
            public class R { }
            """);

#if DEBUG
        Assert.Single(Scan(Ledger([])).LiveEdges);
#else
        Assert.Empty(Scan(Ledger([])).LiveEdges);
#endif
    }

    [Fact]
    public void RelativeName_ResolvesAgainstItsOwnNamespaceBlock()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Mixed.cs", """
            namespace Cluckwork.Temp.Hub
            {
                public class H { }
            }
            namespace Cluckwork.Temp.Red
            {
                public class R { public string Go() => Blue.B.Name; }
            }
            """);

        var edge = Assert.Single(Scan(Ledger([])).LiveEdges);

        Assert.Equal(("Red", "Blue", "Cluckwork.Temp.Red.R"), (edge.From, edge.To, edge.Symbol));
    }

    [Fact]
    public void GlobalAliasToAnExactRoot_IsAFailure()
    {
        OwnerDefinition[] owners =
        [
            Owner("Hub", "platform", "Cluckwork.Temp.Hub", "Cluckwork.Temp"),
            Owner("Red", "module", "Cluckwork.Temp.Red"),
            Owner("Blue", "module", "Cluckwork.Temp.Blue"),
        ];
        WriteSource("src/Root.cs", "namespace Cluckwork.Temp;\npublic class Root { }\n");
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Globals.cs", "global using T = Cluckwork.Temp;\n");

        var failure = Evaluate(Ledger([], owners))
            .FirstOrDefault(f => f.Contains("global using of module namespace"));

        Assert.False(string.IsNullOrEmpty(failure), "expected the root alias to be rejected");
        Assert.Contains("'Cluckwork.Temp'", failure!);
    }

    [Fact]
    public void SingleIdentifierRelativeImport_IsAnEdge()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red
            {
                using Blue;
                public class R { }
            }
            """);

        var edge = Assert.Single(Scan(Ledger([])).LiveEdges);

        Assert.Equal(("Red", "Blue", "Cluckwork.Temp.Red.R"), (edge.From, edge.To, edge.Symbol));
    }

    [Fact]
    public void SingleIdentifierAliasToALocalType_IsNotAnEdge()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", """
            namespace Cluckwork.Temp.Red
            {
                using Alias = Blue;
                public class Blue { }
                public class R { public Alias? Value; }
            }
            """);

        Assert.Empty(Scan(Ledger([])).LiveEdges);
    }

    [Fact]
    public void TempTreeFloor_IsItsOwnFileCount()
    {
        WriteSource("src/Blue.cs", BlueSource);
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");
        WriteSource("src/Hub.cs", "namespace Cluckwork.Temp.Hub;\npublic class H { }\n");

        var report = Scan(Ledger([]));

        Assert.Equal(3, report.ScannedFileCount);
        Assert.Equal(3, report.ExpectedFileCountFloor);
        Assert.DoesNotContain(ModuleLedgerScanner.Evaluate(report), f => f.Contains("the walk saw less than it should"));
    }

    [Fact]
    public void DuplicateOwnerName_IsARegistryError()
    {
        OwnerDefinition[] owners =
        [
            Owner("Red", "module", "Cluckwork.Temp.Red"),
            Owner("Red", "module", "Cluckwork.Temp.Blue"),
        ];
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(Ledger([], owners));

        Assert.Contains("duplicate owner 'Red'", failure);
    }

    [Fact]
    public void NamespaceClaimedByTwoOwners_IsARegistryError()
    {
        OwnerDefinition[] owners =
        [
            Owner("Red", "module", "Cluckwork.Temp.Red"),
            Owner("Blue", "module", "Cluckwork.Temp.Red"),
        ];
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(Ledger([], owners));

        Assert.Contains("namespace 'Cluckwork.Temp.Red' is claimed 2 times", failure);
    }

    [Fact]
    public void CellNamingAnUnknownOwner_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(Ledger([Cell("Red", "Purple", "Cluckwork.Temp.Red.R")]));

        Assert.Contains("references unknown owner 'Purple'", failure);
    }

    [Fact]
    public void TwoCellsForTheSamePair_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(Ledger([Cell("Red", "Blue", "Cluckwork.Temp.Red.R"), Cell("Red", "Blue", "Cluckwork.Temp.Red.R")]));

        Assert.Contains("duplicate edge cell Red -> Blue", failure);
    }

    [Fact]
    public void SymbolListedTwiceInACell_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(Ledger([
            Cell("Red", "Blue", "Cluckwork.Temp.Red.R", "Cluckwork.Temp.Red.R")]));

        Assert.Contains("lists symbol 'Cluckwork.Temp.Red.R' 2 times", failure);
    }

    [Fact]
    public void CellTouchingAPlatformOwner_IsARegistryError()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(Ledger([Cell("Red", "Hub", "Cluckwork.Temp.Red.R")]));

        Assert.Contains("names platform owner 'Hub'", failure);
    }

    [Fact]
    public void MalformedCell_IsARegistryErrorRatherThanADroppedRow()
    {
        WriteSource("src/Red.cs", "namespace Cluckwork.Temp.Red;\npublic class R { }\n");

        var failure = RegistryFailure(Ledger(
            [new EdgeCell("Red", "Blue", "X", "  ", ["Cluckwork.Temp.Red.R"])]));

        Assert.Contains("has kind 'X'", failure);
        Assert.Contains("blank reason", failure);
    }

    private string RegistryFailure(ModuleLedger ledger)
    {
        var failure = Evaluate(ledger).FirstOrDefault(f => f.Contains("registry error"));
        Assert.False(string.IsNullOrEmpty(failure), "expected a registry-error failure");
        return failure!;
    }
}
