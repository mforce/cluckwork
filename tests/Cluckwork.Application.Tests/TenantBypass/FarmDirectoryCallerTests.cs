using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.TenantBypass;

// #858 — IFarmDirectory reads every farm with the tenant filter off, and it is
// on Farm's contract, so the ledger guards let any adapter take it. Only the
// operator verbs and the jobs may: a request path that names it could show one
// farm another's. Walk every source file and allow only these places. Name-based
// like InsightsReadOnlyTests, so an alias or reflection evades it.
public sealed class FarmDirectoryCallerTests
{
    private static readonly string[] WatchedNames = ["IFarmDirectory", "AccountRepository"];

    private static readonly string[] AllowedFiles =
    [
        "src/Cluckwork.Application/Features/Accounts/IFarmDirectory.cs",
        "src/Cluckwork.Infrastructure/Repositories/AccountRepository.cs",
        "src/Cluckwork.Api/Hosting/CluckworkFeatureServiceCollectionExtensions.cs",
    ];

    private static readonly string[] AllowedDirectories =
    [
        "src/Cluckwork.Api/Cli/",
        "src/Cluckwork.Infrastructure/Jobs/",
    ];

    [Fact]
    public void OnlyOperatorVerbsAndJobsNameTheFarmDirectory()
    {
        var repoRoot = GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");
        var files = GuardScanner.EnumerateSourceFiles(Path.Combine(repoRoot, "src"));

        var namers = files
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .Where(relative => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(repoRoot, relative)))
                .GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(name => WatchedNames.Contains(name.Identifier.ValueText)))
            .ToList();

        var outside = namers
            .Where(relative => !AllowedFiles.Contains(relative)
                && !AllowedDirectories.Any(dir => relative.StartsWith(dir, StringComparison.Ordinal)))
            .ToList();
        Assert.True(outside.Count == 0,
            "These files name IFarmDirectory or AccountRepository outside the operator verbs and jobs. " +
            "The directory ignores the tenant filter, so a request path must not reach it:\n  " +
            string.Join("\n  ", outside));

        // The walk saw the real callers, so an empty result above is not vacuous.
        Assert.Contains("src/Cluckwork.Api/Cli/ListAccountsCliCommand.cs", namers);
        Assert.Contains("src/Cluckwork.Infrastructure/Jobs/DailyEntryLockSweep.cs", namers);
    }
}
