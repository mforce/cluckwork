using Cluckwork.Application.Tests.Architecture;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.TenantBypass;

// #858 — IFarmDirectory reads every farm with the tenant filter off, and it is
// on Farm's contract, so the ledger guards let any adapter take it. This guard
// keeps the NAME of the directory, or of AccountRepository, out of every file
// except the operator verbs, the jobs and the directory's own files. It does
// not follow calls: a forwarder in an allowed place, such as
// AccountSlugLookup.ResolveAsync until #858 P8 deletes it, hands the directory
// to callers this walk never sees.
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
            .Where(relative => NamesWatchedType(File.ReadAllText(Path.Combine(repoRoot, relative))))
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

    // The compiler builds `#if NET10_0` code, so the walk must parse it too.
    [Fact]
    public void ANameInsideAnActiveFrameworkConditional_IsSeen()
    {
        Assert.True(NamesWatchedType("""
            class Endpoint
            {
            #if NET10_0
                void Leak(IFarmDirectory directory) { }
            #endif
            }
            """));
        Assert.False(NamesWatchedType("class Endpoint { void Settings(IFarmModule farm) { } }"));
    }

    private static bool NamesWatchedType(string source) =>
        CSharpSyntaxTree.ParseText(source, ModuleLedgerScanner.ParseOptions)
            .GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(name => WatchedNames.Contains(name.Identifier.ValueText));
}
