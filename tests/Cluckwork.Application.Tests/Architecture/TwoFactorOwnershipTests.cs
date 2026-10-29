using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cluckwork.Application.Tests.Architecture;

// #1278 G3.20: one oracle and one protect site (#1274 "The rule"). A symbol
// walk of src/ binds every name, so an alias, a using static or a renamed
// receiver cannot hide a reference. Each owned reference must sit in
// TwoFactorCredentials, or in a member that OutsideReaders lists for that
// property; read or write makes no difference. Identity's two-factor stores
// are banned outright, by their declaring interface, together with the
// UserManager members that use them, and no method in src/ may take one of
// their names.
public sealed class TwoFactorOwnershipTests
{
    private const string Identity = "Cluckwork.Infrastructure.Modules.Access.Identity.";
    // Members and types are keyed by assembly and symbol, so a same-named
    // declaration in another assembly matches no row and is not the owner.
    private const string InInfrastructure = "Cluckwork.Infrastructure: ";
    private const string Owner = InInfrastructure + Identity + "TwoFactorCredentials";
    private const string Matcher = Identity + "TotpMatcher";
    private const string UserType = Identity + "ApplicationUser";
    private const string ProtectorPurpose = "Cluckwork.TwoFactorCredentials";

    // The owned columns are read off ApplicationUser itself: TwoFactorEnabled
    // and every property named SecondFactor*, so a new one is owned at once.
    // The six #1276 shipped are the floor.
    private const int OwnedColumnFloor = 6;

    // The members outside TwoFactorCredentials allowed to reference an owned
    // column, keyed by the member's assembly and symbol and the property (#859).
    // Maintainer, 2026-10-29: "List the read sites". A new reader adds one
    // reviewed row. A row must name an owned column and exactly one declared
    // method or accessor; a row its member no longer uses fails, and so does a
    // repeated row.
    private sealed record OutsideReader(string Member, string Property, string Reason);

    private static readonly OutsideReader[] OutsideReaders =
    [
        new(PasswordStepRead, "SecondFactorSignIns", "the challenge carries the count the code step compares (decision 2)"),
        new(InInfrastructure + Identity + "IdentityProvider.LoginAsync(System.Guid, string, string, System.Threading.CancellationToken)",
            "TwoFactorEnabled", "a correct password asks an enrolled user for a code (decision 5)"),
        new(InInfrastructure + Identity + "IdentityProvider.RefreshAsync(string, System.Threading.CancellationToken, System.Guid?)",
            "TwoFactorEnabled", "an enrolled lineage ends a fixed time after its last sign-in (decision 8)"),
        new(InInfrastructure + Identity + "JwtTokenService.CreateTokenPair(" + Identity + "ApplicationUser, System.Collections.Generic.IReadOnlyCollection<string>, string)",
            "TwoFactorEnabled", "the two_factor claim the SPA shows step-up's code field from"),
    ];

    // The password step's read of the sign-in count, which the challenge
    // carries (decision 2): the record requires the walk to find it.
    private const string PasswordStepRead = InInfrastructure + Identity + "SecondFactorChallenge.Issue(" + Identity + "ApplicationUser, " + Identity + "JwtOptions, System.DateTimeOffset)";

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
        // It moves the flag with no CredentialEpoch write the epoch rule could see.
        "SetTwoFactorEnabledAsync",
    ];

    // TwoFactorCredentials held 25 owned references on 2026-10-29.
    private const int OwnedFloor = 20;

    private sealed record Hit(string Kind, string Container, string? Method, string Member, string Location);

    private static readonly Lazy<IReadOnlyList<Hit>> RealTree = new(() => Walk(SourceRoot()));

    [Fact]
    public void OnlyTwoFactorCredentials_AndTheListedReaders_ReachTwoFactorState()
    {
        var hits = RealTree.Value;
        var owned = hits.Where(h => h.Kind == "owned").ToList();
        Assert.True(owned.Count(h => h.Container == Owner) >= OwnedFloor,
            $"the walk found {owned.Count(h => h.Container == Owner)} owned references in {Owner}, below its floor of {OwnedFloor}");

        var listed = OutsideReaders.Select(r => (r.Member, r.Property)).ToHashSet();
        var offenders = owned.Where(h => h.Container != Owner && !listed.Contains((h.Method ?? "", h.Member)))
            .Select(h => $"{h.Location} {h.Member} in {h.Method}").ToList();
        Assert.True(offenders.Count == 0, "two-factor state reached outside TwoFactorCredentials and OutsideReaders:\n  "
            + string.Join("\n  ", offenders));
        Assert.Contains(owned, h => h.Method == PasswordStepRead && h.Member == "SecondFactorSignIns");
    }

    [Fact]
    public void EveryOutsideReaderRow_IsUsedOnce()
    {
        var hits = RealTree.Value;
        var used = hits.Where(h => h.Kind == "owned").Select(h => (h.Method ?? "", h.Member)).ToHashSet();
        Assert.True(OutsideReaders.Length >= 4, $"OutsideReaders holds {OutsideReaders.Length} rows, below its floor of 4");
        var columns = hits.Where(h => h.Kind == "column").Select(h => h.Member).ToHashSet();
        var notColumns = OutsideReaders.Where(r => !columns.Contains(r.Property)).Select(r => $"{r.Member} {r.Property}").ToList();
        Assert.True(notColumns.Count == 0, "OutsideReaders rows whose property is not an owned column:\n  " + string.Join("\n  ", notColumns));
        var declared = hits.Where(h => h.Kind == "declares").GroupBy(h => h.Member).ToDictionary(g => g.Key, g => g.Count());
        var unmatched = OutsideReaders.Where(r => declared.GetValueOrDefault(r.Member) != 1)
            .Select(r => $"{r.Member}: {declared.GetValueOrDefault(r.Member)} declarations").ToList();
        Assert.True(unmatched.Count == 0, "OutsideReaders rows that do not name exactly one declared member:\n  " + string.Join("\n  ", unmatched));
        var stale = OutsideReaders.Where(r => !used.Contains((r.Member, r.Property))).Select(r => $"{r.Member} {r.Property}").ToList();
        Assert.True(stale.Count == 0, "OutsideReaders rows no reference uses; delete them:\n  " + string.Join("\n  ", stale));
        var repeated = OutsideReaders.GroupBy(r => (r.Member, r.Property)).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();
        Assert.True(repeated.Count == 0, "OutsideReaders repeats rows:\n  " + string.Join("\n  ", repeated));
    }

    [Fact]
    public void NothingCallsOrNamesIdentitysTwoFactorStores()
    {
        var banned = RealTree.Value.Where(h => h.Kind == "banned").Select(h => $"{h.Location} {h.Member} in {h.Container}").ToList();
        Assert.True(banned.Count == 0, "Identity's two-factor store API is used or named in src/:\n  " + string.Join("\n  ", banned));
    }

    // #1274 decision 8: a factor change ends every session minted before it.
    // Every assignment in src/ counts, wherever it sits (a method, an accessor,
    // an expression-bodied member, a field initializer or a lambda), keyed by
    // its member: a plain, compound or null-conditional assignment, an
    // increment, a deconstruction element at any depth, an object initializer,
    // a SetProperty target, and an assignment to an EF property entry's
    // CurrentValue, directly or through a local. The count is one-sided: any
    // Entry(...).Property(u => u.TwoFactorEnabled), even a read, counts as a
    // flag write, while CredentialEpoch counts only when it is really written.
    [Fact]
    public void EveryMethodThatAssignsTwoFactorEnabled_AlsoAssignsCredentialEpoch()
    {
        var hits = RealTree.Value.Where(h => h.Kind is "assigns" or "selects").ToList();
        var enabling = hits.Where(h => h.Member == "TwoFactorEnabled").GroupBy(h => h.Method)
            .ToDictionary(g => g.Key ?? "", g => g.Any(h => h.Kind == "assigns")
                ? "assigns it"
                : "selects it through Entry(...).Property, which counts as a write even when it only reads");
        Assert.NotEmpty(enabling);
        var bumping = hits.Where(h => h.Kind == "assigns" && h.Member == "CredentialEpoch").Select(h => h.Method ?? "").ToHashSet();
        var missing = enabling.Where(e => !bumping.Contains(e.Key)).Select(e => $"{e.Key} {e.Value}").ToList();
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

        var hits = owned.Select(column => new Hit("column", UserType, null, column, UserType)).ToList();
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
            return new Hit(kind, container is null ? "<none>" : Key(container), method is null ? null : Key(method), member, $"{file}:{line}");
        }

        foreach (var declaration in root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax))
            if (model.GetDeclaredSymbol(declaration) is IMethodSymbol declared && declared.PartialImplementationPart is null)
                yield return new Hit("declares", "", null, Key(declared), file);

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => BannedManagerMembers.Contains(m.Identifier.ValueText) || StoreMemberNames.Contains(m.Identifier.ValueText)))
            yield return At(method, "banned", "declares " + method.Identifier.ValueText);

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.Token.ValueText == ProtectorPurpose))
            yield return At(literal, "owned", "protector purpose");

        // A column named in SQL or any other string. Schema text is exempt: the
        // ApplicationUser entity configuration, and in a migration or the
        // snapshot a literal bound to a column-naming parameter of EF's schema
        // API. Anything else, Sql, UpdateData, a filter or a local, fails.
        var migration = GuardScanner.IsMigrationShaped(file);
        foreach (var text in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken)
                || t.IsKind(SyntaxKind.InterpolatedStringTextToken) || t.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                || t.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)))
        {
            if (migration ? IsSchemaColumnName(text.Parent!, model) : IsEntityConfiguration(text.Parent!, model)) continue;
            foreach (var column in ownedColumns.Where(c => text.ValueText.Contains(c, StringComparison.Ordinal)))
                yield return At(text.Parent!, "owned", column + " in a string");
        }

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

            if (symbol is IPropertySymbol property && IsUserColumn(property)
                && ownedColumns.Contains(property.Name) && !IsEntityConfiguration(name, model))
                yield return At(name, "owned", property.Name);
        }

        // Every user-column assignment and every flag selection through an EF
        // property entry, keyed by its member, for the epoch rule.
        foreach (var node in root.DescendantNodes().Where(n => n is AssignmentExpressionSyntax
                     or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or InvocationExpressionSyntax))
        {
            var operation = model.GetOperation(node);
            foreach (var target in AssignedProperties(operation).Where(IsUserColumn))
                yield return At(node, "assigns", target.Name);
            if (operation is IInvocationOperation invocation && EntrySelection(invocation).Any(p => p.Name == "TwoFactorEnabled" && IsUserColumn(p)))
                yield return At(node, "selects", "TwoFactorEnabled");
        }
    }

    private static string Key(ISymbol symbol) => $"{symbol.ContainingAssembly?.Name}: {symbol.ToDisplayString()}";

    private static IEnumerable<IPropertySymbol> AssignedProperties(IOperation? operation) => operation switch
    {
        IAssignmentOperation assignment => Targets(assignment.Target),
        IIncrementOrDecrementOperation increment => Targets(increment.Target),
        IInvocationOperation { TargetMethod.Name: "SetProperty" } setter =>
            setter.Arguments.Where(a => a.Parameter?.Name == "propertyExpression").SelectMany(a => Selected(a.Value)),
        _ => [],
    };

    private static IEnumerable<IPropertySymbol> Targets(IOperation target) => target switch
    {
        IPropertyReferenceOperation { Property.Name: "CurrentValue", Instance: { } entry } => EntryOf(entry),
        IPropertyReferenceOperation reference => [reference.Property],
        ITupleOperation tuple => tuple.Elements.SelectMany(Targets),
        _ => [],
    };

    // The property an EF property entry selects, the entry given inline or as
    // a local initialized from Entry(...).Property(selector). A local's
    // reassignment is not followed, and an initializer through ?. or a local
    // with none resolves to nothing.
    private static IEnumerable<IPropertySymbol> EntryOf(IOperation entry) => entry switch
    {
        IInvocationOperation invocation => EntrySelection(invocation),
        ILocalReferenceOperation local => local.Local.DeclaringSyntaxReferences
            .Select(reference => local.SemanticModel?.GetOperation(reference.GetSyntax()))
            .OfType<IVariableDeclaratorOperation>()
            .Select(declarator => Unconverted(declarator.Initializer?.Value))
            .OfType<IInvocationOperation>()
            .SelectMany(EntrySelection),
        _ => [],
    };

    private static IOperation? Unconverted(IOperation? operation) =>
        operation is IConversionOperation conversion ? Unconverted(conversion.Operand) : operation;

    private static IEnumerable<IPropertySymbol> EntrySelection(IInvocationOperation invocation) =>
        invocation.TargetMethod is { Name: "Property" } method
        && method.ContainingType.OriginalDefinition.ToDisplayString() == "Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity>"
            ? invocation.Arguments.Where(a => a.Parameter?.Name == "propertyExpression").SelectMany(a => Selected(a.Value))
            : [];

    // The property a selector lambda such as u => u.TwoFactorEnabled names.
    private static IEnumerable<IPropertySymbol> Selected(IOperation selector) =>
        selector.DescendantsAndSelf().OfType<IPropertyReferenceOperation>().Take(1).Select(reference => reference.Property);

    // The banned stores' member names, so no method in src/ can take one.
    private static readonly HashSet<string> StoreMemberNames =
    [
        "ReplaceCodesAsync", "RedeemCodeAsync", "CountCodesAsync",
        "SetTokenAsync", "RemoveTokenAsync", "GetTokenAsync",
        "SetAuthenticatorKeyAsync", "GetAuthenticatorKeyAsync",
        "SetTwoFactorEnabledAsync",
    ];

    // A store member is banned by its declaring interface, or by implementing one.
    private static bool IsBannedIdentityMember(IMethodSymbol method)
    {
        var declaring = method.ContainingType.OriginalDefinition;
        if (IsBannedStoreMember(method)) return true;
        if (declaring.ToDisplayString() == "Microsoft.AspNetCore.Identity.UserManager<TUser>")
            return BannedManagerMembers.Contains(method.Name);
        return method.ContainingType.AllInterfaces
            .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
            .Where(IsBannedStoreMember)
            .Any(member => SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(member), method));
    }

    private static bool IsBannedStoreMember(IMethodSymbol method)
    {
        var declaring = method.ContainingType.OriginalDefinition.ToDisplayString();
        return BannedStores.Contains(declaring)
            || (declaring == "Microsoft.AspNetCore.Identity.IUserTwoFactorStore<TUser>" && method.Name == "SetTwoFactorEnabledAsync");
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

    // The schema operations of EF's migration and model builders, each with the
    // parameters that name a column or an index. InsertData and UpdateData
    // take a column too, but they write rows, so they are not here.
    private static readonly HashSet<string> SchemaColumnParameters =
    [
        "AddColumn.name", "AlterColumn.name", "DropColumn.name", "RenameColumn.name", "RenameColumn.newName",
        "CreateIndex.name", "CreateIndex.column", "CreateIndex.columns", "DropIndex.name",
        "Property.propertyName", "HasIndex.propertyNames",
    ];

    private static readonly HashSet<string> SchemaBuilders =
        ["Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder", "Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder"];

    private static bool IsSchemaColumnName(SyntaxNode literal, SemanticModel model)
    {
        var operation = model.GetOperation(literal);
        while (operation?.Parent is IConversionOperation or IArrayInitializerOperation or IArrayCreationOperation or ICollectionExpressionOperation)
            operation = operation.Parent;
        return operation?.Parent is IArgumentOperation { Parameter: { } parameter, Parent: IInvocationOperation invocation }
            && SchemaColumnParameters.Contains($"{invocation.TargetMethod.Name}.{parameter.Name}")
            && SchemaBuilders.Contains(invocation.TargetMethod.ContainingType.ToDisplayString());
    }
}
