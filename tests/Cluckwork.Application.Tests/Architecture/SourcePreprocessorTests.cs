using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Architecture;

public sealed class SourcePreprocessorTests
{
    [Fact]
    public void RealSourceTree_HasNoInactiveCode()
    {
        var repoRoot = GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");
        var files = GuardScanner.EnumerateSourceFiles(Path.Combine(repoRoot, "src"));
        Assert.True(files.Count >= GuardScanner.RealTreeFileFloor,
            $"source walk saw only {files.Count} files, expected at least {GuardScanner.RealTreeFileFloor}");

        var inactive = files.SelectMany(file => FindInactiveLines(File.ReadAllText(file))
            .Select(line => $"{Path.GetRelativePath(repoRoot, file).Replace('\\', '/')}:{line}"))
            .ToList();
        Assert.True(inactive.Count == 0,
            "source guards cannot inspect inactive C# regions:\n  " + string.Join("\n  ", inactive));
    }

    [Theory]
    [InlineData("#if FOO\nclass Hidden {}\n#endif", 2)]
    [InlineData("#if !NET10_0\nclass Hidden {}\n#endif", 2)]
    [InlineData("#if NET10_0\nclass Visible {}\n#else\nclass Hidden {}\n#endif", 4)]
    [InlineData("#if FOO\nclass Hidden {}\n#elif NET10_0\nclass Visible {}\n#endif", 2)]
    [InlineData("#if NET10_0\nclass Visible {}\n#elif FOO\nclass Hidden {}\n#endif", 4)]
    public void InactiveRegion_IsReported(string source, int line)
    {
        Assert.Equal([line], FindInactiveLines(source));
    }

    // NETCOREAPP3_1_OR_GREATER is an SDK compatibility symbol a hand list
    // omitted (#1053); the guards read the build's own DefineConstants now.
    [Theory]
    [InlineData("NET10_0")]
    [InlineData("NETCOREAPP3_1_OR_GREATER")]
    public void ActiveFrameworkRegion_IsAccepted(string symbol)
    {
        Assert.Empty(FindInactiveLines($"#if {symbol}\nclass Visible {{}}\n#endif"));
    }

    [Fact]
    public void ConfigurationBranch_MatchesTheTestBuild()
    {
        const string source = """
            #if DEBUG
            class DebugOnly {}
            #elif RELEASE
            class ReleaseOnly {}
            #else
            class OtherConfiguration {}
            #endif
            """;
        var names = CSharpSyntaxTree.ParseText(source, ModuleLedgerScanner.ParseOptions)
            .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(type => type.Identifier.ValueText).ToList();
#if DEBUG
        Assert.Equal(["DebugOnly"], names);
#elif RELEASE
        Assert.Equal(["ReleaseOnly"], names);
#else
        Assert.Equal(["OtherConfiguration"], names);
#endif
    }

    private static List<int> FindInactiveLines(string source) =>
        CSharpSyntaxTree.ParseText(source, ModuleLedgerScanner.ParseOptions)
            .GetRoot().DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            .Select(trivia => trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToList();
}
