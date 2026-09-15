namespace Cluckwork.Application.Tests.Architecture;

public sealed class AdapterTierTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("adapter-tier-").FullName;

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    private void WriteSource(string relativePath, string content)
    {
        var full = Path.Combine(_tempRoot, "src", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string WriteLedger(string tiers = "")
    {
        var path = Path.Combine(_tempRoot, "module-ledger.json");
        File.WriteAllText(path, """
            {
              "owners": { "Hub": { "kind": "platform", "namespaces": ["Cluckwork.Temp"] } },
              "edges": [],
              "adapterTiers": [
            """ + tiers + "]\n}\n");
        return path;
    }

    private AdapterTierReport Scan(string tiers = "")
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        return AdapterTierScanner.Scan(Path.Combine(_tempRoot, "src"), WriteLedger(tiers));
    }

    private static string Tier(string ns = "Cluckwork.Temp.Mcp", string privilege = "DirectRepository",
        string surface = "MapMcp", string reason = "test reason", string reviewBy = "#1") =>
        $$"""{ "namespace": "{{ns}}", "privilege": "{{privilege}}", "surface": "{{surface}}", "reason": "{{reason}}", "reviewBy": "{{reviewBy}}" }""";

    [Fact]
    public void ToolTypeOutsideAnyTierNamespace_IsToolTypeOutsideTier()
    {
        WriteSource("Probe.cs", """
            namespace Cluckwork.Temp.Endpoints;
            [McpServerToolType]
            public sealed class Probe { }
            """);
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan(Tier())));
        Assert.Contains("Cluckwork.Temp.Endpoints.Probe", failure);
        Assert.Contains("src/Probe.cs:2", failure);
    }

    [Fact]
    public void ToolTypeUnderTierNamespace_IsGreenAndNotDormant()
    {
        WriteSource("Tools.cs", """
            namespace Cluckwork.Temp.Mcp;
            [McpServerToolType]
            public sealed class WaterTools { }
            """);
        var report = Scan(Tier());
        Assert.Empty(AdapterTierScanner.Evaluate(report));
        Assert.Empty(report.Dormant);
    }

    [Fact]
    public void SurfaceCallWithNoTierRow_IsSurfaceWithoutTier()
    {
        WriteSource("Program.cs", "app.MapMcp(\"/mcp\");");
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan()));
        Assert.Contains("MapMcp", failure);
        Assert.Contains("src/Program.cs:1", failure);
        Assert.Contains("\"surface\": \"MapMcp\"", failure);
    }

    [Fact]
    public void TierRowWithNoInvocationAndNoTypes_IsDormantAndGreen()
    {
        var report = Scan(Tier());
        Assert.Empty(AdapterTierScanner.Evaluate(report));
        var dormant = Assert.Single(report.Dormant);
        Assert.Equal("Cluckwork.Temp.Mcp", dormant.Namespace);
    }

    [Theory]
    [InlineData("[McpServerToolTypeAttribute]")]
    [InlineData("[ModelContextProtocol.Server.McpServerToolType]")]
    public void QualifiedNameOrAttributeSuffix_IsDetected(string attribute)
    {
        WriteSource("Tools.cs", $$"""
            namespace Cluckwork.Temp.Mcp;
            {{attribute}}
            public sealed class WaterTools { }
            """);
        var report = Scan(Tier());
        Assert.Empty(AdapterTierScanner.Evaluate(report));
        Assert.Empty(report.Dormant);
    }

    [Fact]
    public void FileLocalAliasToToolAttribute_OutsideTier_IsReported()
    {
        WriteSource("Probe.cs", """
            using ToolMarker = ModelContextProtocol.Server.McpServerToolTypeAttribute;
            namespace Cluckwork.Temp.Endpoints;
            [ToolMarker]
            public sealed class Probe { }
            """);
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan(Tier())));
        Assert.Contains("Cluckwork.Temp.Endpoints.Probe", failure);
        Assert.Contains("src/Probe.cs:3", failure);
    }

    [Fact]
    public void GlobalAliasFromAnotherFileInSameProject_IsDetected()
    {
        WriteSource("ProjA/Aliases.cs", """
            global using ToolMarker = ModelContextProtocol.Server.McpServerToolTypeAttribute;
            namespace Cluckwork.Temp.ProjA;
            """);
        WriteSource("ProjA/Probe.cs", """
            namespace Cluckwork.Temp.Endpoints;
            [ToolMarker]
            public sealed class Probe { }
            """);
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan(Tier())));
        Assert.Contains("Cluckwork.Temp.Endpoints.Probe", failure);
        Assert.Contains("src/ProjA/Probe.cs:2", failure);
    }

    [Fact]
    public void AliasToUnrelatedType_DoesNotMatch()
    {
        WriteSource("Probe.cs", """
            using NotATool = System.ObsoleteAttribute;
            namespace Cluckwork.Temp.Endpoints;
            [NotATool]
            public sealed class Probe { }
            """);
        Assert.Empty(Scan(Tier()).ToolTypeOutsideTier);
    }

    [Fact]
    public void SurfaceCallInsideLambdaOrLocalFunction_IsDetected()
    {
        WriteSource("Program.cs", """
            void Configure() { app.MapMcp("/mcp"); }
            System.Action a = () => { app.MapMcp("/mcp"); };
            """);
        var report = Scan();
        Assert.Equal(2, report.SurfaceWithoutTier.Count);
    }

    [Fact]
    public void ParseError_MakesTheWalkUntrusted()
    {
        WriteSource("Broken.cs", "namespace Cluckwork.Temp.Other; public class Broken {");
        Assert.Contains("the walk cannot be trusted", Assert.Single(AdapterTierScanner.Evaluate(Scan())));
    }

    [Fact]
    public void BlankField_IsRegistryError()
    {
        var row = """{ "namespace": " ", "privilege": "DirectRepository", "surface": "MapMcp", "reason": "r", "reviewBy": "#1" }""";
        Assert.Contains(Scan(row).RegistryErrors, e => e.Contains("'namespace'", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingField_IsRegistryError()
    {
        var row = """{ "privilege": "DirectRepository", "surface": "MapMcp", "reason": "r", "reviewBy": "#1" }""";
        Assert.Contains(Scan(row).RegistryErrors, e => e.Contains("'namespace'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownPrivilege_IsRegistryError()
    {
        var row = Tier(privilege: "ReadOnlyRepository");
        Assert.Contains(Scan(row).RegistryErrors,
            e => e.Contains("privilege", StringComparison.Ordinal) && e.Contains("ReadOnlyRepository", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownSurface_IsRegistryError()
    {
        var row = Tier(surface: "MapMcpTypo");
        Assert.Contains(Scan(row).RegistryErrors,
            e => e.Contains("surface", StringComparison.Ordinal) && e.Contains("MapMcpTypo", StringComparison.Ordinal));
    }

    [Fact]
    public void ReviewByNotMatchingPattern_IsRegistryError()
    {
        var row = Tier(reviewBy: "806");
        Assert.Contains(Scan(row).RegistryErrors, e => e.Contains("reviewBy", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateNamespace_IsRegistryError()
    {
        var rows = Tier(surface: "MapMcp") + "," + Tier(surface: "MapOther");
        Assert.Contains(Scan(rows).RegistryErrors,
            e => e.Contains("duplicate", StringComparison.Ordinal) && e.Contains("namespace", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateSurface_IsRegistryError()
    {
        var rows = Tier(ns: "Cluckwork.Temp.Mcp") + "," + Tier(ns: "Cluckwork.Temp.Other");
        Assert.Contains(Scan(rows).RegistryErrors,
            e => e.Contains("duplicate", StringComparison.Ordinal) && e.Contains("surface", StringComparison.Ordinal));
    }
}
