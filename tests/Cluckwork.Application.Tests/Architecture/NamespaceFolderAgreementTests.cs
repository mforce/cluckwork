using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cluckwork.Application.Tests.Architecture;

// #1087: a file's namespace is its folder, so a contract read from a Contracts namespace sits in a Contracts folder.
// Reflection sees only Type.Namespace; without this rule a file moved out of Contracts/ that kept its namespace would
// stay contract.
public sealed class NamespaceFolderAgreementTests
{
    private static readonly string[] Projects =
        ["Cluckwork.Domain", "Cluckwork.Application", "Cluckwork.Infrastructure", "Cluckwork.Api"];

    // #1086's rules files declare the namespace of the attributes they apply; the analyzer reads them there.
    private const string RulesDirectory = "Cluckwork.Domain/Common/Architecture/Modules";

    [Fact]
    public void EverySourceFile_DeclaresItsFolderAsItsNamespace()
    {
        var src = Path.Combine(GuardScanner.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found"), "src");
        var files = Projects
            .SelectMany(project => GuardScanner.EnumerateSourceFiles(Path.Combine(src, project))
                .Select(file => Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/')))
            .ToList();

        var violations = files.Select(f => Violation(f, File.ReadAllText(Path.Combine(src, f)))).OfType<string>().ToList();

        Assert.True(files.Count >= 500, $"read only {files.Count} source files");
        Assert.True(violations.Count == 0, "namespace differs from folder:\n  " + string.Join("\n  ", violations));
    }

    [Theory]
    [InlineData("Cluckwork.Application/Modules/Example/Contracts/ExampleCommand.cs",
        "namespace Cluckwork.Application.Modules.Example.Contracts; public record ExampleCommand;", null)]
    [InlineData("Cluckwork.Application/Modules/Example/Things/DoThing/ExampleCommand.cs",
        "namespace Cluckwork.Application.Modules.Example.Contracts; public record ExampleCommand;",
        "declares namespace 'Cluckwork.Application.Modules.Example.Contracts'; its folder is 'Cluckwork.Application.Modules.Example.Things.DoThing'")]
    [InlineData("Cluckwork.Application/Modules/Example/Contracts/ExampleCommand.cs",
        "namespace Cluckwork.Application.Modules.Example.Things.DoThing; public record ExampleCommand;",
        "declares namespace 'Cluckwork.Application.Modules.Example.Things.DoThing'; its folder is 'Cluckwork.Application.Modules.Example.Contracts'")]
    [InlineData("Cluckwork.Domain/Things/Two.cs",
        "namespace Cluckwork.Domain.Things { } namespace Cluckwork.Domain.Things.Contracts { }",
        "declares 2 namespaces; declare exactly one")]
    [InlineData("Cluckwork.Domain/Things/Global.cs", "public record Loose;", "declares types without a namespace")]
    [InlineData("Cluckwork.Domain/GlobalUsings.cs", "global using System;", null)]
    [InlineData("Cluckwork.Api/Program.cs", "return 0; public partial class Program { }", null)]
    [InlineData("Cluckwork.Domain/Common/Architecture/Modules/Example.cs",
        "namespace Cluckwork.Domain.Common.Architecture; class ExampleModuleRules;", null)]
    [InlineData("Cluckwork.Domain/Common/Architecture/Modules/Example.cs",
        "namespace Cluckwork.Domain.Common.Architecture.Modules; class ExampleModuleRules;",
        "declares namespace 'Cluckwork.Domain.Common.Architecture.Modules'; its folder is 'Cluckwork.Domain.Common.Architecture'")]
    public void Violation_ComparesTheDeclaredNamespaceWithTheFolder(string path, string source, string? expected) =>
        Assert.Equal(expected is null ? null : $"{path}: {expected}", Violation(path, source));

    // path is relative to src/, with '/' separators.
    private static string? Violation(string path, string source)
    {
        var root = CSharpSyntaxTree.ParseText(source, ModuleLedgerScanner.ParseOptions).GetRoot();
        var namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().ToList();
        var directory = path[..path.LastIndexOf('/')];
        var folder = directory == RulesDirectory ? "Cluckwork.Domain.Common.Architecture" : directory.Replace('/', '.');

        return namespaces switch
        {
            [] when root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                .Select(m => m switch
                {
                    BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
                    DelegateDeclarationSyntax d => d.Identifier.ValueText,
                    _ => null,
                })
                .All(type => type is null || (path == "Cluckwork.Api/Program.cs" && type == "Program")) => null,
            [] => $"{path}: declares types without a namespace",
            [var single] when single.Name.ToString() == folder => null,
            [var single] => $"{path}: declares namespace '{single.Name}'; its folder is '{folder}'",
            _ => $"{path}: declares {namespaces.Count} namespaces; declare exactly one",
        };
    }
}
