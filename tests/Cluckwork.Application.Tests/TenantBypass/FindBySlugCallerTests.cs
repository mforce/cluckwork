using Cluckwork.Application.Tests.Architecture;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.TenantBypass;

// #1053 — IAccountRepository.FindBySlugAsync ignores the tenant filter and
// returns any farm's whole Account. IAccountRepository is Farm's seam, so every
// peer module may take it and the ledger guards stay green on a new caller.
// This guard allows a REFERENCE to the member only inside login's method, keyed
// by enclosing symbol (#632), so another method in IdentityProvider fails too.
// Declarations are not references, so the interface and its implementation
// need no exemption. Like FarmDirectoryCallerTests it does not follow calls:
// ResolveFarmCodeAsync is the forwarder, and it hands out only the account id
// and active flag.
public sealed class FindBySlugCallerTests
{
    private const string Member = "FindBySlugAsync";

    private const string Login =
        "Cluckwork.Infrastructure.Modules.Access.Identity.IdentityProvider.ResolveFarmCodeAsync(string farmCode, CancellationToken ct)";

    [Fact]
    public void OnlyLoginCallsTheCrossFarmSlugLookup()
    {
        var repoRoot = GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found");

        var references = GuardScanner.EnumerateSourceFiles(Path.Combine(repoRoot, "src"))
            .SelectMany(file => References(File.ReadAllText(file))
                .Select(name => GuardScanner.EnclosingSymbolOf(name, file)))
            .ToList();

        var outside = references.Where(symbol => symbol != Login).ToList();
        Assert.True(outside.Count == 0,
            $"These members reference IAccountRepository.{Member}, which reads any farm's account " +
            "with the tenant filter off. Only login may call it:\n  " +
            string.Join("\n  ", outside));

        // The walk saw login's call, so an empty result above is not vacuous.
        Assert.Contains(Login, references);
    }

    [Theory]
    [InlineData("class H { Task Run(IAccountRepository a) => a.FindBySlugAsync(\"x\"); }")]
    [InlineData("class H { Task Run(IAccountRepository? a) => a?.FindBySlugAsync(\"x\"); }")]
    [InlineData("class H { Func<string, CancellationToken, Task<Account?>> F(IAccountRepository a) => a.FindBySlugAsync; }")]
    [InlineData("class H { Task Run(IServiceProvider s) => s.GetRequiredService<IAccountRepository>().FindBySlugAsync(\"x\"); }")]
    [InlineData("class H { string N = nameof(IAccountRepository.FindBySlugAsync); }")]
    [InlineData("class H {\n#if NET10_0\n Task Run(IAccountRepository a) => a.FindBySlugAsync(\"x\");\n#endif\n}")]
    [InlineData("class H {\n#if NETCOREAPP3_1_OR_GREATER\n Task Run(IAccountRepository a) => a.FindBySlugAsync(\"x\");\n#endif\n}")]
    public void AReferenceIsSeen(string source) => Assert.NotEmpty(References(source));

    [Fact]
    public void ADeclarationIsNotAReference() =>
        Assert.Empty(References(
            "class R : IAccountRepository { public Task<Account?> FindBySlugAsync(string s, CancellationToken ct) => null!; }"));

    private static IEnumerable<IdentifierNameSyntax> References(string source) =>
        CSharpSyntaxTree.ParseText(source, ModuleLedgerScanner.ParseOptions)
            .GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(name => name.Identifier.ValueText == Member);
}
