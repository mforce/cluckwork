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

    private AdapterTierReport Scan(params AdapterTier[] tiers)
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        return AdapterTierScanner.Scan(Path.Combine(_tempRoot, "src"), ModuleLedger.Validate(
            new ModuleLedger([new("Hub", "platform", ["Cluckwork.Temp"], [])], [], []) { AdapterTiers = tiers }));
    }

    private static AdapterTier Tier(string ns = "Cluckwork.Temp.Mcp", string privilege = "DirectRepository",
        string surface = "MapMcp", string reason = "test reason", string reviewBy = "#1") =>
        new(ns, privilege, surface, reason, reviewBy);

    private void WriteCsproj(string projectName, string defineConstants)
    {
        var full = Path.Combine(_tempRoot, "src", projectName, $"{projectName}.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <DefineConstants>{defineConstants}</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void CsprojDefinesUndeclaredConstant_IsParseTrustFailure()
    {
        WriteCsproj("Probe", "MCP");
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan()));
        Assert.Contains("the walk cannot be trusted", failure);
        Assert.Contains("MCP", failure);
    }

    [Fact]
    public void CsprojDefineConstantsPlaceholderPlusDeclaredSymbol_IsGreen()
    {
        WriteCsproj("Probe", "$(DefineConstants);TRACE");
        Assert.Empty(AdapterTierScanner.Evaluate(Scan()));
    }

    [Fact]
    public void DirectoryBuildPropsUnderSrc_DefinesUndeclaredConstant_IsParseTrustFailure()
    {
        WriteFile("src/Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <DefineConstants>MCP</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan()));
        Assert.Contains("the walk cannot be trusted", failure);
        Assert.Contains("MCP", failure);
    }

    [Fact]
    public void ImportedPropsFile_DefinesUndeclaredConstant_IsParseTrustFailure()
    {
        WriteFile("src/Shared/Shared.props", """
            <Project>
              <PropertyGroup>
                <DefineConstants>MCP</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        WriteFile("src/Probe/Probe.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="../Shared/Shared.props" />
            </Project>
            """);
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan()));
        Assert.Contains("the walk cannot be trusted", failure);
        Assert.Contains("MCP", failure);
    }

    [Fact]
    public void ConditionedDefineConstants_IsParseTrustFailure()
    {
        WriteFile("src/Probe/Probe.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <DefineConstants Condition="'$(Configuration)'=='Debug'">MCP</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan()));
        Assert.Contains("Condition", failure);
        Assert.Contains("MCP", failure);
    }

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
        Assert.EndsWith("add the row to PlatformModuleRules in src/Cluckwork.Domain/Common/Architecture/Modules/Platform.cs:\n" +
            "[AdapterTier(\"<the tool namespace>\", \"DirectRepository\", " +
            "\"MapMcp\", \"<why this surface needs the privilege, with a citation>\", \"<#issue>\")]", failure);
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
    public void LocalAliasChainOfTwo_OutsideTier_IsReported()
    {
        WriteSource("Probe.cs", """
            using ActualMarker = ModelContextProtocol.Server.McpServerToolTypeAttribute;
            using ToolMarker = ActualMarker;
            namespace Cluckwork.Temp.Endpoints;
            [ToolMarker]
            public sealed class Probe { }
            """);
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan(Tier())));
        Assert.Contains("Cluckwork.Temp.Endpoints.Probe", failure);
        Assert.Contains("src/Probe.cs:4", failure);
    }

    [Fact]
    public void GlobalThenLocalAliasChain_OutsideTier_IsReported()
    {
        WriteSource("ProjA/Aliases.cs", """
            global using GlobalMarker = ModelContextProtocol.Server.McpServerToolTypeAttribute;
            namespace Cluckwork.Temp.ProjA;
            """);
        WriteSource("ProjA/Probe.cs", """
            using ToolMarker = GlobalMarker;
            namespace Cluckwork.Temp.Endpoints;
            [ToolMarker]
            public sealed class Probe { }
            """);
        var failure = Assert.Single(AdapterTierScanner.Evaluate(Scan(Tier())));
        Assert.Contains("Cluckwork.Temp.Endpoints.Probe", failure);
        Assert.Contains("src/ProjA/Probe.cs:3", failure);
    }

    [Fact]
    public void CyclicAlias_DoesNotHangAndMatchesNothing()
    {
        WriteSource("Probe.cs", """
            using A = B;
            using B = A;
            namespace Cluckwork.Temp.Endpoints;
            [A]
            public sealed class Probe { }
            """);
        Assert.Empty(Scan(Tier()).ToolTypeOutsideTier);
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
        var row = Tier(ns: " ", reason: "r");
        Assert.Contains(Scan(row).RegistryErrors, e => e.Contains("'namespace'", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingField_IsRegistryError()
    {
        var row = Tier(ns: "", reason: "r");
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
    public void PrivilegeDoesNotMatchSurfaceMapping_IsRegistryError()
    {
        var row = Tier(privilege: "ReadOnlyRepository");
        Assert.Contains(Scan(row).RegistryErrors,
            e => e.Contains("privilege", StringComparison.Ordinal)
                && e.Contains("MapMcp", StringComparison.Ordinal)
                && e.Contains("DirectRepository", StringComparison.Ordinal));
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
        Assert.Contains(Scan(Tier(surface: "MapMcp"), Tier(surface: "MapOther")).RegistryErrors,
            e => e.Contains("duplicate", StringComparison.Ordinal) && e.Contains("namespace", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateSurface_IsRegistryError()
    {
        Assert.Contains(Scan(Tier(ns: "Cluckwork.Temp.Mcp"), Tier(ns: "Cluckwork.Temp.Other")).RegistryErrors,
            e => e.Contains("duplicate", StringComparison.Ordinal) && e.Contains("surface", StringComparison.Ordinal));
    }
}
