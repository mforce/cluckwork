using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cluckwork.Application.Tests.Architecture;

// #1278 G3.20: one oracle and one protect site (#1274 "The rule"). A symbol
// walk of src/ binds every name, so an alias, a using static or a renamed
// receiver cannot hide a reference. Each owned reference must sit in
// TwoFactorCredentials. Identity's two-factor stores are banned outright, by
// their declaring interface, together with the UserManager members that use
// them, and no method in src/ may take one of their names.
public sealed class TwoFactorOwnershipTests
{
    private const string Owner = "Cluckwork.Infrastructure.Modules.Access.Identity.TwoFactorCredentials";
    private const string Matcher = "Cluckwork.Infrastructure.Modules.Access.Identity.TotpMatcher";
    private const string UserType = "Cluckwork.Infrastructure.Modules.Access.Identity.ApplicationUser";
    private const string ProtectorPurpose = "Cluckwork.TwoFactorCredentials";

    // The owned columns are read off ApplicationUser itself: TwoFactorEnabled
    // and every property named SecondFactor*, so a new one is owned at once.
    // The six #1276 shipped are the floor.
    private const int OwnedColumnFloor = 6;

    // Login and refresh read these from the row they already loaded.
    private static readonly HashSet<string> ReadAnywhere = ["TwoFactorEnabled", "SecondFactorSignIns"];

    private static readonly HashSet<string> BannedStores =
    [
        "Microsoft.AspNetCore.Identity.IUserTwoFactorRecoveryCodeStore<TUser>",
        "Microsoft.AspNetCore.Identity.IUserAuthenticationTokenStore<TUser>",
        "Microsoft.AspNetCore.Identity.IUserAuthenticatorKeyStore<TUser>",
    ];

    // The UserManager members that read or write those stores' rows.
    private static readonly HashSet<string> BannedManagerMembers =
    [
        "ResetAuthenticatorKeyAsync", "GetAuthenticatorKeyAsync", "GenerateNewTwoFactorRecoveryCodesAsync",
        "RedeemTwoFactorRecoveryCodeAsync", "CountRecoveryCodesAsync", "SetAuthenticationTokenAsync",
        "GetAuthenticationTokenAsync", "RemoveAuthenticationTokenAsync",
    ];

    // TwoFactorCredentials held 25 owned references on 2026-10-29.
    private const int OwnedFloor = 20;

    private sealed record Hit(string Kind, string Container, string? Method, string Member, string Location);

    private static readonly Lazy<IReadOnlyList<Hit>> RealTree = new(() => Walk(SourceRoot()));

    [Fact]
    public void OnlyTwoFactorCredentials_ReachesTwoFactorState()
    {
        var hits = RealTree.Value;
        var owned = hits.Where(h => h.Kind == "owned").ToList();
        Assert.True(owned.Count(h => h.Container == Owner) >= OwnedFloor,
            $"the walk found {owned.Count(h => h.Container == Owner)} owned references in {Owner}, below its floor of {OwnedFloor}");
        Assert.Contains(hits, h => h.Kind == "allowed-read" && h.Member == "SecondFactorSignIns" && h.Container != Owner);

        var offenders = owned.Where(h => h.Container != Owner).Select(h => $"{h.Location} in {h.Container}").ToList();
        Assert.True(offenders.Count == 0, "two-factor state reached outside TwoFactorCredentials:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NothingCallsOrNamesIdentitysTwoFactorStores()
    {
        var banned = RealTree.Value.Where(h => h.Kind == "banned").Select(h => $"{h.Location} {h.Member} in {h.Container}").ToList();
        Assert.True(banned.Count == 0, "Identity's two-factor store API is used or named in src/:\n  " + string.Join("\n  ", banned));
    }

    // #1274 decision 8: a factor change ends every session minted before it.
    [Fact]
    public void EveryMethodThatWritesTwoFactorEnabled_AlsoWritesCredentialEpoch()
    {
        var hits = RealTree.Value;
        var enabling = hits.Where(h => h.Member == "TwoFactorEnabled:write").ToList();
        Assert.NotEmpty(enabling);
        var bumping = hits.Where(h => h.Kind == "epoch-write").Select(h => h.Method).ToHashSet();
        var missing = enabling.Where(h => !bumping.Contains(h.Method)).Select(h => $"{h.Location} in {h.Method}").ToList();
        Assert.True(missing.Count == 0, "TwoFactorEnabled is written without a CredentialEpoch write in:\n  " + string.Join("\n  ", missing));
    }

    private static string SourceRoot() => Path.Combine(
        GuardScanner.FindRepoRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("Cluckwork.slnx not found"), "src");

    private static IReadOnlyList<Hit> Walk(string srcRoot)
    {
        var infrastructure = CompatibilityExceptionScanner.Compile(
            CompatibilityExceptionScanner.SemanticProject,
            GuardScanner.EnumerateSourceFiles(Path.Combine(srcRoot, CompatibilityExceptionScanner.SemanticProject)).Select(CompatibilityExceptionScanner.Parse),
            CompatibilityExceptionScanner.ImplicitUsings,
            CompatibilityExceptionScanner.References(CompatibilityExceptionScanner.SemanticProject));
        var errors = infrastructure.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(5).ToList();
        Assert.True(errors.Count == 0, "the Infrastructure compile has errors, so names would bind to nothing:\n" + string.Join("\n", errors));

        var dependents = CompatibilityExceptionScanner.ProjectsReferencing(srcRoot, CompatibilityExceptionScanner.SemanticProject)
            .Select(project => CompatibilityExceptionScanner.Compile(project,
                GuardScanner.EnumerateSourceFiles(Path.Combine(srcRoot, project)).Select(CompatibilityExceptionScanner.Parse),
                CompatibilityExceptionScanner.ImplicitUsings + CompatibilityExceptionScanner.WebImplicitUsings,
                CompatibilityExceptionScanner.References(CompatibilityExceptionScanner.SemanticProject).Append(infrastructure.ToMetadataReference())));

        var user = infrastructure.GetTypeByMetadataName(UserType) ?? throw new InvalidOperationException(UserType + " not found");
        var owned = user.GetMembers().OfType<IPropertySymbol>().Select(p => p.Name)
            .Where(n => n.StartsWith("SecondFactor", StringComparison.Ordinal)).Append("TwoFactorEnabled").ToHashSet();
        Assert.True(owned.Count >= OwnedColumnFloor, $"found {owned.Count} owned columns on {UserType}, below its floor of {OwnedColumnFloor}");

        var hits = new List<Hit>();
        foreach (var compilation in dependents.Prepend(infrastructure))
            foreach (var tree in compilation.SyntaxTrees.Where(t => t.FilePath.Length > 0))
                hits.AddRange(WalkTree(compilation.GetSemanticModel(tree), tree, srcRoot, owned));
        return hits;
    }

    private static IEnumerable<Hit> WalkTree(SemanticModel model, SyntaxTree tree, string srcRoot, HashSet<string> ownedColumns)
    {
        var root = tree.GetRoot();
        var file = Path.GetRelativePath(srcRoot, tree.FilePath).Replace('\\', '/');

        Hit At(SyntaxNode node, string kind, string member)
        {
            var enclosing = model.GetEnclosingSymbol(node.SpanStart);
            var method = enclosing;
            while (method is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
                method = method.ContainingSymbol;
            var container = enclosing?.ContainingType ?? enclosing as INamedTypeSymbol;
            while (container?.ContainingType is { } outer) container = outer;
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            return new Hit(kind, container?.ToDisplayString() ?? "<none>", method?.ToDisplayString(), member, $"{file}:{line}");
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => BannedManagerMembers.Contains(m.Identifier.ValueText) || StoreMemberNames.Contains(m.Identifier.ValueText)))
            yield return At(method, "banned", "declares " + method.Identifier.ValueText);

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.Token.ValueText == ProtectorPurpose))
            yield return At(literal, "owned", "protector purpose");

        // A column named in SQL or any other string, except the EF migrations
        // and model snapshot, which describe the schema rather than use it.
        if (!IsMigrationShaped(file))
            foreach (var text in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken)
                    || t.IsKind(SyntaxKind.InterpolatedStringTextToken) || t.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                    || t.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)))
                foreach (var column in ownedColumns.Where(c => text.ValueText.Contains(c, StringComparison.Ordinal)))
                    yield return At(text.Parent!, "owned", column + " in a string");

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name).Symbol;
            if (symbol is null) continue;

            if (symbol is IMethodSymbol method && IsBannedIdentityMember(method))
            {
                yield return At(name, "banned", method.ToDisplayString());
                continue;
            }

            var containing = symbol is INamedTypeSymbol type ? type : symbol.ContainingType;
            if (containing?.OriginalDefinition.ToDisplayString() == Matcher)
            {
                if (!name.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => model.GetDeclaredSymbol(t)?.ToDisplayString() == Matcher))
                    yield return At(name, "owned", "TotpMatcher");
                continue;
            }

            if (symbol is IPropertySymbol { Name: "UserTokens" } tokens
                && tokens.ContainingType.ContainingNamespace.ToDisplayString() == "Microsoft.AspNetCore.Identity.EntityFrameworkCore")
            {
                yield return At(name, "owned", "UserTokens");
                continue;
            }

            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                && ctor.ContainingType.OriginalDefinition.ToDisplayString() == "Microsoft.AspNetCore.Identity.IdentityUserToken<TKey>")
            {
                yield return At(name, "owned", "new IdentityUserToken");
                continue;
            }

            if (symbol is IMethodSymbol identityMethod && IsTwoFactorIdentityMember(identityMethod))
            {
                yield return At(name, "owned", identityMethod.Name);
                continue;
            }

            if (symbol is IPropertySymbol property && IsUserColumn(property))
            {
                var written = IsWrite(name, model);
                if (property.Name == "CredentialEpoch")
                {
                    if (written) yield return At(name, "epoch-write", "CredentialEpoch");
                    continue;
                }
                if (!ownedColumns.Contains(property.Name)) continue;
                if (IsEntityConfiguration(name, model)) continue;
                if (!written && ReadAnywhere.Contains(property.Name))
                    yield return At(name, "allowed-read", property.Name);
                else
                    yield return At(name, "owned", property.Name + (written ? ":write" : ":read"));
            }
        }
    }

    // The banned stores' member names, so no method in src/ can take one.
    private static readonly HashSet<string> StoreMemberNames =
    [
        "ReplaceCodesAsync", "RedeemCodeAsync", "CountCodesAsync",
        "SetTokenAsync", "RemoveTokenAsync", "GetTokenAsync",
        "SetAuthenticatorKeyAsync", "GetAuthenticatorKeyAsync",
    ];

    // A store member is banned by its declaring interface, or by implementing one.
    private static bool IsBannedIdentityMember(IMethodSymbol method)
    {
        var declaring = method.ContainingType.OriginalDefinition;
        if (BannedStores.Contains(declaring.ToDisplayString())) return true;
        if (declaring.ToDisplayString() == "Microsoft.AspNetCore.Identity.UserManager<TUser>")
            return BannedManagerMembers.Contains(method.Name);
        return method.ContainingType.AllInterfaces
            .Where(i => BannedStores.Contains(i.OriginalDefinition.ToDisplayString()))
            .SelectMany(i => i.GetMembers())
            .Any(member => SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(member), method));
    }

    // Identity's other TwoFactor, Authenticator and RecoveryCode members on
    // UserManager and its stores: allowed, but only to the owner.
    private static bool IsTwoFactorIdentityMember(IMethodSymbol method)
    {
        var declaring = method.ContainingType.OriginalDefinition.ToDisplayString();
        var identityType = declaring == "Microsoft.AspNetCore.Identity.UserManager<TUser>"
            || (declaring.StartsWith("Microsoft.AspNetCore.Identity.IUser", StringComparison.Ordinal) && declaring.EndsWith("Store<TUser>", StringComparison.Ordinal));
        return identityType && (method.Name.Contains("TwoFactor", StringComparison.Ordinal)
            || method.Name.Contains("Authenticator", StringComparison.Ordinal)
            || method.Name.Contains("RecoveryCode", StringComparison.Ordinal));
    }

    private static bool IsUserColumn(IPropertySymbol property)
    {
        for (var type = property.ContainingType; type is not null; type = type.BaseType)
            if (type.ToDisplayString() == UserType) return true;
        return property.ContainingType.OriginalDefinition.ToDisplayString() == "Microsoft.AspNetCore.Identity.IdentityUser<TKey>"
            && property.Name == "TwoFactorEnabled";
    }

    // The IEntityTypeConfiguration<ApplicationUser> that would set column defaults.
    private static bool IsEntityConfiguration(SyntaxNode node, SemanticModel model) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => model.GetDeclaredSymbol(t) is INamedTypeSymbol type
            && type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<TEntity>"
                && i.TypeArguments.Single().ToDisplayString() == UserType));

    // An assignment, compound or deconstruction target, an increment, or the
    // property an EF SetProperty call names, bound through the operation tree.
    private static bool IsWrite(SimpleNameSyntax name, SemanticModel model)
    {
        SyntaxNode node = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
        var current = model.GetOperation(node);
        while (current?.Parent is { } parent)
        {
            if (parent is IIncrementOrDecrementOperation) return true;
            if (parent is IAssignmentOperation assignment) return assignment.Target == current;
            if (parent is not ITupleOperation) break;
            current = parent;
        }
        var argument = name.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault();
        return argument?.Parent?.Parent is InvocationExpressionSyntax invocation
            && ((ArgumentListSyntax)argument.Parent).Arguments.IndexOf(argument) == 0
            && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "SetProperty" };
    }

    // GlobalUserLookupGuardTests' rule: an EF snapshot, or a 14-digit timestamped
    // migration and its designer, under a Migrations directory.
    private static bool IsMigrationShaped(string file)
    {
        var parts = file.Split('/');
        if (parts.Length < 2 || parts[^2] != "Migrations") return false;
        var name = parts[^1];
        return name == "AppDbContextModelSnapshot.cs"
            || (name.Length > 15 && name[14] == '_' && name[..14].All(char.IsAsciiDigit));
    }
}
