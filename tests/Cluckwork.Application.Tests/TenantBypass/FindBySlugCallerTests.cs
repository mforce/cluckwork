using Cluckwork.Application.Tests.Architecture;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.TenantBypass;

// #1053 — IAccountRepository.FindBySlugAsync ignores the tenant filter and
// returns any farm's whole Account. IAccountRepository is Farm's seam, so every
// peer module may take it and the ledger guards stay green on a new caller.
// This guard keeps a REFERENCE to the member out of every file but login's.
// Declarations are not references, so the interface and its implementation
// need no exemption. Like FarmDirectoryCallerTests it does not
// follow calls: IIdentityProvider.ResolveFarmCodeAsync is the forwarder, and it
// hands out only the account id and active flag.
public sealed class FindBySlugCallerTests
{
    private const string Member = "FindBySlugAsync";

    private static readonly string[] AllowedFiles =
    [
        "src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs",
    ];

    [Fact]
    public void OnlyLoginCallsTheCrossFarmSlugLookup()
    {
        var repoRoot = GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");

        var callers = GuardScanner.EnumerateSourceFiles(Path.Combine(repoRoot, "src"))
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .Where(relative => ReferencesMember(File.ReadAllText(Path.Combine(repoRoot, relative))))
            .ToList();

        var outside = callers.Except(AllowedFiles).ToList();
        Assert.True(outside.Count == 0,
            $"These files reference IAccountRepository.{Member}, which reads any farm's account " +
            "with the tenant filter off. Only login may call it:\n  " +
            string.Join("\n  ", outside));

        // The walk saw login's call, so an empty result above is not vacuous.
        Assert.Contains(AllowedFiles[0], callers);
    }

    [Theory]
    [InlineData("class H { Task Run(IAccountRepository a) => a.FindBySlugAsync(\"x\"); }")]
    [InlineData("class H { Task Run(IAccountRepository? a) => a?.FindBySlugAsync(\"x\"); }")]
    [InlineData("class H { Func<string, CancellationToken, Task<Account?>> F(IAccountRepository a) => a.FindBySlugAsync; }")]
    [InlineData("class H { Task Run(IServiceProvider s) => s.GetRequiredService<IAccountRepository>().FindBySlugAsync(\"x\"); }")]
    [InlineData("class H { string N = nameof(IAccountRepository.FindBySlugAsync); }")]
    [InlineData("class H {\n#if NET10_0\n Task Run(IAccountRepository a) => a.FindBySlugAsync(\"x\");\n#endif\n}")]
    public void AReferenceIsSeen(string source) => Assert.True(ReferencesMember(source));

    [Fact]
    public void ADeclarationIsNotAReference() =>
        Assert.False(ReferencesMember(
            "class R : IAccountRepository { public Task<Account?> FindBySlugAsync(string s, CancellationToken ct) => null!; }"));

    private static bool ReferencesMember(string source) =>
        CSharpSyntaxTree.ParseText(source, ModuleLedgerScanner.ParseOptions)
            .GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(name => name.Identifier.ValueText == Member);
}
