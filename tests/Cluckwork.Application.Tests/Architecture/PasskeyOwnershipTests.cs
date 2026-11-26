using Cluckwork.Application.Tests.TenantBypass;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cluckwork.Application.Tests.Architecture;

// #1284 G7.6: one owner of passkey state (record 1282 "The rule"). A symbol
// walk of src/ finds every reference to Identity's passkey handler and
// options, UserManager's and IUserPasskeyStore's passkey members, the
// UserPasskeys set, the PasskeyCeremonies set and ApplicationUser.PasskeyCount.
// Each must sit in PasskeyCredentials, an entity configuration of the passkey,
// ceremony or user type, or a member OutsideOwner lists with a reason, keyed by
// assembly and display signature as G3.20 is. Set<PasskeyCeremony>() is refused
// everywhere but the PasskeyCeremonies getter itself, because the tenant-bypass
// legs see the ceremony table only through that set.
public sealed class PasskeyOwnershipTests
{
    private const string Identity = "Cluckwork.Infrastructure.Modules.Access.Identity.";
    private const string InInfrastructure = "Cluckwork.Infrastructure: ";
    private const string Owner = InInfrastructure + Identity + "PasskeyCredentials";
    private const string UserType = Identity + "ApplicationUser";
    private const string CeremonyType = Identity + "PasskeyCeremony";
    private const string CeremonyGetter = InInfrastructure + "Cluckwork.Infrastructure.Persistence.AppDbContext.PasskeyCeremonies.get";

    // The owned names, each a kind of reference the walk reports.
    private const string Handler = "PasskeyHandler";
    private const string Options = "IdentityPasskeyOptions";
    private const string ManagerOrStore = "a UserManager or IUserPasskeyStore passkey member";
    private const string UserPasskeys = "UserPasskeys";
    private const string Ceremonies = "PasskeyCeremonies";
    private const string CeremonySet = "Set<PasskeyCeremony>()";
    private const string Count = "PasskeyCount";

    private sealed record OutsideMember(string Member, string Name, string Reason);

    private static readonly OutsideMember[] OutsideOwner =
    [
        new("Cluckwork.Api: Cluckwork.Api.Hosting.CluckworkIdentityServiceCollectionExtensions.AddCluckworkIdentity(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration, Microsoft.Extensions.Hosting.IHostEnvironment, Cluckwork.Api.Hosting.ProcessRole)",
            Handler, "AddIdentityCore registers no passkey handler, so the registration adds it (record 1282 decision 2)"),
        new("Cluckwork.Api: Cluckwork.Api.Hosting.CluckworkIdentityServiceCollectionExtensions.AddCluckworkIdentity(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration, Microsoft.Extensions.Hosting.IHostEnvironment, Cluckwork.Api.Hosting.ProcessRole)",
            Options, "the handler's options are bound in code: attestation none, UV and resident key required, the origin hook (decision 4)"),
        new(InInfrastructure + Identity + "PasskeyCeremonyPurge.DeleteExpiredBatchAsync(System.DateTimeOffset, int, System.Threading.CancellationToken)",
            Ceremonies, "the ceremony sweep deletes expired rows in its own class, as RefreshTokenPurge does (decision 3)"),
        new(InInfrastructure + Identity + "IdentityProvider.ListUsersAsync(System.Guid, System.Threading.CancellationToken)",
            Count, "the Users page shows the count and offers Remove passkeys when it is above 0 (decision 18)"),
        new(InInfrastructure + Identity + "TwoFactorCredentials.SecondFactorsOf(" + Identity + "ApplicationUser)",
            Count, "the proofs a user can give at the second step: login's pending state and the code step read it, also while passkeys are off (decision 7, #1285)"),
    ];

    // PasskeyCredentials held 15 owned references on 2026-11-23, and three
    // of its members wrote passkey rows.
    private const int OwnerFloor = 10;
    private const int OwnedNameFloor = 3;

    private sealed record Hit(string Kind, string Container, string Method, string Name, string Location);

    private static readonly Lazy<IReadOnlyList<Hit>> RealTree = new(() => Walk(SourceRoot()));

    [Fact]
    public void OnlyPasskeyCredentials_AndTheListedMembers_ReachPasskeyState()
    {
        var owned = RealTree.Value.Where(h => h.Kind == "owned").ToList();
        var inOwner = owned.Where(h => h.Container == Owner).ToList();
        Assert.True(inOwner.Count >= OwnerFloor,
            $"the walk found {inOwner.Count} owned references in {Owner}, below its floor of {OwnerFloor}");
        var names = owned.Select(h => h.Name).Distinct().Count();
        Assert.True(names >= OwnedNameFloor, $"the walk found {names} owned names, below its floor of {OwnedNameFloor}");

        var listed = OutsideOwner.Select(r => (r.Member, r.Name)).ToHashSet();
        var offenders = owned.Where(h => h.Container != Owner && !listed.Contains((h.Method, h.Name)))
            .Select(h => $"{h.Location} {h.Name} in {h.Method}").ToList();
        Assert.True(offenders.Count == 0, "passkey state reached outside PasskeyCredentials and OutsideOwner:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryOutsideOwnerRow_IsUsedOnce_AndNamesOneDeclaredMember()
    {
        var hits = RealTree.Value;
        Assert.All(OutsideOwner, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason), $"{r.Member} {r.Name} has no reason"));
        var used = hits.Where(h => h.Kind == "owned").Select(h => (h.Method, h.Name)).ToHashSet();
        var stale = OutsideOwner.Where(r => !used.Contains((r.Member, r.Name))).Select(r => $"{r.Member} {r.Name}").ToList();
        Assert.True(stale.Count == 0, "OutsideOwner rows no reference uses; delete them:\n  " + string.Join("\n  ", stale));
        var repeated = OutsideOwner.GroupBy(r => (r.Member, r.Name)).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();
        Assert.True(repeated.Count == 0, "OutsideOwner repeats rows:\n  " + string.Join("\n  ", repeated));
        var declared = hits.Where(h => h.Kind == "declares").GroupBy(h => h.Method).ToDictionary(g => g.Key, g => g.Count());
        var unmatched = OutsideOwner.Where(r => declared.GetValueOrDefault(r.Member) != 1)
            .Select(r => $"{r.Member}: {declared.GetValueOrDefault(r.Member)} declarations").ToList();
        Assert.True(unmatched.Count == 0, "OutsideOwner rows that do not name exactly one declared member:\n  " + string.Join("\n  ", unmatched));
    }

    // The tenant-bypass legs see the ceremony table only through the
    // PasskeyCeremonies set, so Set<PasskeyCeremony>() may appear only in that
    // set's own getter, exempt by its symbol, and exactly once.
    [Fact]
    public void SetOfPasskeyCeremony_AppearsOnlyInThePasskeyCeremoniesGetter()
    {
        var sets = RealTree.Value.Where(h => h.Kind == "set").ToList();
        var outside = sets.Where(h => h.Method != CeremonyGetter).Select(h => $"{h.Location} in {h.Method}").ToList();
        Assert.True(outside.Count == 0, "Set<PasskeyCeremony>() outside the PasskeyCeremonies getter:\n  " + string.Join("\n  ", outside));
        Assert.Single(sets, h => h.Method == CeremonyGetter);
    }

    // Decision 6: the count is exact under concurrency only if every member
    // that adds or deletes passkey rows also writes PasskeyCount.
    [Fact]
    public void EveryMemberThatAddsOrDeletesPasskeyRows_AlsoWritesPasskeyCount()
    {
        var hits = RealTree.Value;
        var writers = hits.Where(h => h.Kind == "writes rows").Select(h => h.Method).ToHashSet();
        Assert.True(writers.Count >= 3, $"the walk found {writers.Count} members that write passkey rows, below its floor of 3");
        var counting = hits.Where(h => h.Kind == "writes count").Select(h => h.Method).ToHashSet();
        var missing = writers.Where(w => !counting.Contains(w)).ToList();
        Assert.True(missing.Count == 0, "passkey rows are added or deleted without a PasskeyCount write in:\n  " + string.Join("\n  ", missing));
    }

    private static string SourceRoot() => Path.Combine(
        GuardScanner.FindRepoRoot(AppContext.BaseDirectory) ?? throw new InvalidOperationException("Cluckwork.slnx not found"), "src");

    private static IReadOnlyList<Hit> Walk(string srcRoot)
    {
        var (infrastructure, dependents) = TwoFactorOwnershipTests.InfrastructureAndDependents(srcRoot);
        var hits = new List<Hit>();
        foreach (var compilation in dependents.Prepend(infrastructure))
            foreach (var tree in compilation.SyntaxTrees.Where(t => t.FilePath.Length > 0))
                hits.AddRange(WalkTree(compilation.GetSemanticModel(tree), tree, srcRoot));
        return hits;
    }

    private static IEnumerable<Hit> WalkTree(SemanticModel model, SyntaxTree tree, string srcRoot)
    {
        var root = tree.GetRoot();
        var file = Path.GetRelativePath(srcRoot, tree.FilePath).Replace('\\', '/');
        if (GuardScanner.IsMigrationShaped(file)) yield break;

        // The outermost type declaration is the container, so a primary
        // constructor's parameter, which has no enclosing member, still has one.
        Hit At(SyntaxNode node, string kind, string name)
        {
            var method = model.GetEnclosingSymbol(node.SpanStart);
            while (method is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
                method = method.ContainingSymbol;
            var outermost = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().LastOrDefault();
            var container = outermost is null ? null : model.GetDeclaredSymbol(outermost);
            if (method is null or INamespaceSymbol) method = container;
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            return new Hit(kind, container is null ? "<none>" : Key(container), method is null ? "<none>" : Key(method), name, $"{file}:{line}");
        }

        foreach (var declaration in root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax))
            if (model.GetDeclaredSymbol(declaration) is IMethodSymbol declared && declared.PartialImplementationPart is null)
                yield return new Hit("declares", "", Key(declared), "", file);

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (IsEntityConfiguration(name, model)) continue;
            var symbol = model.GetSymbolInfo(name).Symbol;
            if (symbol is null || OwnedName(symbol) is not { } owned) continue;
            yield return At(name, "owned", owned);
        }

        foreach (var node in root.DescendantNodes().Where(n => n is AssignmentExpressionSyntax
                     or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or InvocationExpressionSyntax))
        {
            var operation = model.GetOperation(node);
            if (Assigned(operation) is IPropertySymbol { Name: Count } target && Declares(target.ContainingType, UserType))
                yield return At(node, "writes count", Count);
            if (operation is not IInvocationOperation invocation) continue;
            if (WritesPasskeyRows(invocation))
                yield return At(node, "writes rows", invocation.TargetMethod.Name);
            if (invocation.TargetMethod is { Name: "Set", TypeArguments: [var entity] }
                && entity.ToDisplayString() == CeremonyType)
                yield return At(node, "set", CeremonySet);
        }
    }

    private static string? OwnedName(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        var definition = type?.OriginalDefinition.ToDisplayString();
        return symbol switch
        {
            _ when definition is "Microsoft.AspNetCore.Identity.PasskeyHandler<TUser>" or "Microsoft.AspNetCore.Identity.IPasskeyHandler<TUser>" => Handler,
            _ when definition is "Microsoft.AspNetCore.Identity.IdentityPasskeyOptions" => Options,
            IMethodSymbol method when definition is "Microsoft.AspNetCore.Identity.UserManager<TUser>" && method.Name.Contains("Passkey", StringComparison.Ordinal) => ManagerOrStore,
            IMethodSymbol when definition is "Microsoft.AspNetCore.Identity.IUserPasskeyStore<TUser>" => ManagerOrStore,
            IMethodSymbol method when ImplementsPasskeyStore(method) => ManagerOrStore,
            IPropertySymbol { Name: UserPasskeys } when type!.ContainingNamespace.ToDisplayString() == "Microsoft.AspNetCore.Identity.EntityFrameworkCore" => UserPasskeys,
            IPropertySymbol { Name: Ceremonies } when Declares(type, "Cluckwork.Infrastructure.Persistence.AppDbContext") => Ceremonies,
            IPropertySymbol { Name: Count } when Declares(type, UserType) => Count,
            _ => null,
        };
    }

    // A store class's own passkey methods count as the interface's.
    private static bool ImplementsPasskeyStore(IMethodSymbol method) =>
        method.ContainingType.AllInterfaces
            .Where(i => i.OriginalDefinition.ToDisplayString() == "Microsoft.AspNetCore.Identity.IUserPasskeyStore<TUser>")
            .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
            .Any(member => SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(member), method));

    // A write that adds or deletes passkey rows: UserManager's or the store's
    // add and remove, a set's Add, Remove, Attach or Update, or a bulk delete
    // over the passkey entity.
    private static bool WritesPasskeyRows(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (OwnedName(method) == ManagerOrStore)
            return method.Name.StartsWith("AddOrUpdate", StringComparison.Ordinal) || method.Name.StartsWith("Remove", StringComparison.Ordinal);
        var entity = method switch
        {
            { Name: "ExecuteDelete" or "ExecuteDeleteAsync" } => (invocation.Arguments.FirstOrDefault()?.Value.Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault(),
            { Name: "Add" or "AddAsync" or "AddRange" or "AddRangeAsync" or "Remove" or "RemoveRange" or "Attach" or "Update" }
                when method.ContainingType.OriginalDefinition.ToDisplayString() == "Microsoft.EntityFrameworkCore.DbSet<TEntity>" => method.ContainingType.TypeArguments[0],
            _ => null,
        };
        return entity?.OriginalDefinition.ToDisplayString() == "Microsoft.AspNetCore.Identity.IdentityUserPasskey<TKey>";
    }

    private static IPropertySymbol? Assigned(IOperation? operation) => operation switch
    {
        IAssignmentOperation { Target: IPropertyReferenceOperation reference } => reference.Property,
        IIncrementOrDecrementOperation { Target: IPropertyReferenceOperation reference } => reference.Property,
        _ => null,
    };

    private static bool Declares(INamedTypeSymbol? type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == name) return true;
        return false;
    }

    // The entity configurations of the passkey, the ceremony and the user,
    // which map the table and set the count's default and check constraint.
    private static bool IsEntityConfiguration(SyntaxNode node, SemanticModel model) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => model.GetDeclaredSymbol(t) is INamedTypeSymbol type
            && type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<TEntity>"
                && i.TypeArguments.Single().ToDisplayString() is UserType or CeremonyType or "Microsoft.AspNetCore.Identity.IdentityUserPasskey<System.Guid>"));

    private static string Key(ISymbol symbol) => $"{symbol.ContainingAssembly?.Name}: {symbol.ToDisplayString()}";
}
