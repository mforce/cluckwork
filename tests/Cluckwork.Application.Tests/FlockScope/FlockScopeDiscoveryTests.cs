namespace Cluckwork.Application.Tests.FlockScope;

using System.Linq.Expressions;
using System.Reflection;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

// #613 — derive flock-filter coverage from the EF model. A mapped scalar
// FlockId is scoped by default; exclusions are deliberate and reviewable.
public sealed class FlockScopeDiscoveryTests
{
    private static readonly IReadOnlyDictionary<Type, string> Exclusions =
        new Dictionary<Type, string>
        {
            [typeof(UserRoleAssignment)] =
                "Scope-source rows cannot be filtered by the flock scope they resolve.",
        };

    private static DbContextOptions<AppDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unreachable;Username=unreachable;Password=unreachable")
            // Query filters capture a context instance in EF's cached model.
            // Isolate this guard so another test cannot supply that instance.
            .EnableServiceProviderCaching(false)
            .Options;

    [Fact]
    public void EveryMappedFlockEntity_HasCombinedTenantAndFlockFilter()
    {
        // Model-only construction: no connection is opened by building the model.
        var tenant = new TenantContext();
        var flockScope = new FlockScope();
        using var db = new AppDbContext(BuildOptions(), tenant, flockScope);

        var discovered = db.Model.GetEntityTypes()
            .Where(entity => entity.ClrType == typeof(Flock)
                || entity.FindProperty(nameof(UserRoleAssignment.FlockId)) is not null)
            .OrderBy(entity => entity.ClrType.FullName, StringComparer.Ordinal)
            .ToList();

        // Secondary floor only. Discovery above is the authority, so a ninth
        // guarded type expands coverage instead of asking someone to update a list.
        Assert.True(discovered.Count >= 9,
            $"Expected at least 9 flock-related mapped types; discovered {discovered.Count}.");

        var excluded = discovered.Where(entity => Exclusions.ContainsKey(entity.ClrType)).ToList();
        Assert.Equal([typeof(UserRoleAssignment)], excluded.Select(entity => entity.ClrType));
        Assert.All(Exclusions, exclusion =>
        {
            Assert.Contains(discovered, entity => entity.ClrType == exclusion.Key);
            Assert.False(string.IsNullOrWhiteSpace(exclusion.Value));
        });

        var guarded = discovered.Where(entity => !Exclusions.ContainsKey(entity.ClrType)).ToList();
        Assert.True(guarded.Count >= 8,
            $"Expected at least 8 structurally guarded types; discovered {guarded.Count}.");

        foreach (var entity in guarded)
            AssertCombinedFilter(entity, tenant, flockScope);
    }

    private static void AssertCombinedFilter(
        IEntityType entity,
        TenantContext tenant,
        FlockScope flockScope)
    {
        var filters = entity.GetDeclaredQueryFilters();
        Assert.True(filters.Count == 1,
            $"{entity.ClrType.Name}: expected one combined query filter, found {filters.Count}.");

        var filter = filters.Single().Expression;
        Assert.NotNull(filter);
        var parameter = Assert.Single(filter.Parameters);
        var conjuncts = Flatten(filter.Body, ExpressionType.AndAlso).ToList();
        Assert.True(conjuncts.Count == 2,
            $"{entity.ClrType.Name}: filter must be exactly tenant AND flock scope.");

        var accountProperty = entity.FindProperty("AccountId");
        Assert.NotNull(accountProperty);
        var accountConjuncts = conjuncts
            .Where(expression => IsAccountEquality(
                expression, parameter, accountProperty, tenant))
            .ToList();
        Assert.True(accountConjuncts.Count == 1,
            $"{entity.ClrType.Name}: filter must contain e.AccountId == tenant.AccountId.");

        var flockConjunct = Assert.Single(
            conjuncts, expression => expression != accountConjuncts[0]);
        var disjuncts = Flatten(flockConjunct, ExpressionType.OrElse).ToList();

        var keyProperty = entity.ClrType == typeof(Flock)
            ? entity.FindProperty(nameof(Flock.Id))
            : entity.FindProperty(nameof(UserRoleAssignment.FlockId));
        Assert.NotNull(keyProperty);
        var nullable = keyProperty.ClrType == typeof(Guid?);
        Assert.True(keyProperty.ClrType == typeof(Guid) || nullable,
            $"{entity.ClrType.Name}: FlockId must be Guid or nullable Guid.");

        var expectedCount = nullable ? 3 : 2;
        Assert.True(disjuncts.Count == expectedCount,
            $"{entity.ClrType.Name}: expected {expectedCount} flock-scope alternatives, found {disjuncts.Count}.");
        Assert.True(disjuncts.Count(expression => IsUnrestricted(expression, flockScope)) == 1,
            $"{entity.ClrType.Name}: filter must contain flockScope.IsUnrestricted exactly once.");
        Assert.True(disjuncts.Count(expression =>
                IsAssignedContains(expression, parameter, keyProperty, nullable, flockScope)) == 1,
            $"{entity.ClrType.Name}: filter must test the assigned flock ids exactly once.");
        Assert.True(disjuncts.Count(expression =>
                IsNullFlock(expression, parameter, keyProperty)) == (nullable ? 1 : 0),
            nullable
                ? $"{entity.ClrType.Name}: nullable FlockId must keep the farm-wide null branch."
                : $"{entity.ClrType.Name}: non-nullable flock keys cannot have a null branch.");
    }

    private static IEnumerable<Expression> Flatten(Expression expression, ExpressionType nodeType)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression binary && binary.NodeType == nodeType)
        {
            foreach (var child in Flatten(binary.Left, nodeType))
                yield return child;
            foreach (var child in Flatten(binary.Right, nodeType))
                yield return child;
            yield break;
        }

        yield return expression;
    }

    private static bool IsAccountEquality(
        Expression expression,
        ParameterExpression parameter,
        IProperty accountProperty,
        TenantContext tenant) =>
        expression is BinaryExpression { NodeType: ExpressionType.Equal } binary
        && ((IsEntityMember(binary.Left, parameter, accountProperty, nullableValue: false)
                && IsCapturedProperty(binary.Right, nameof(TenantContext.AccountId), tenant))
            || (IsEntityMember(binary.Right, parameter, accountProperty, nullableValue: false)
                && IsCapturedProperty(binary.Left, nameof(TenantContext.AccountId), tenant)));

    private static bool IsUnrestricted(Expression expression, FlockScope flockScope) =>
        IsCapturedProperty(expression, nameof(FlockScope.IsUnrestricted), flockScope);

    private static bool IsAssignedContains(
        Expression expression,
        ParameterExpression parameter,
        IProperty keyProperty,
        bool nullable,
        FlockScope flockScope)
    {
        expression = StripConvert(expression);
        if (expression is not MethodCallExpression
            {
                Object: null,
                Method.Name: nameof(Enumerable.Contains),
                Method.DeclaringType: not null,
            } call
            || call.Method.DeclaringType != typeof(Enumerable)
            || !call.Method.IsGenericMethod
            || call.Method.GetGenericArguments() is not [var elementType]
            || elementType != typeof(Guid)
            || call.Arguments.Count != 2)
            return false;

        return IsCapturedProperty(
                call.Arguments[0], nameof(FlockScope.AssignedFlockIds), flockScope)
            && IsEntityMember(call.Arguments[1], parameter, keyProperty, nullable);
    }

    private static bool IsNullFlock(
        Expression expression,
        ParameterExpression parameter,
        IProperty keyProperty)
    {
        expression = StripConvert(expression);
        return expression is BinaryExpression { NodeType: ExpressionType.Equal } binary
            && ((IsEntityMember(binary.Left, parameter, keyProperty, nullableValue: false)
                    && IsNull(binary.Right))
                || (IsEntityMember(binary.Right, parameter, keyProperty, nullableValue: false)
                    && IsNull(binary.Left)));
    }

    private static bool IsEntityMember(
        Expression expression,
        ParameterExpression parameter,
        IProperty mappedProperty,
        bool nullableValue)
    {
        if (nullableValue)
        {
            if (expression is not MemberExpression valueMember
                || valueMember.Member != typeof(Guid?).GetProperty(nameof(Nullable<Guid>.Value))
                || valueMember.Expression is null)
                return false;
            expression = valueMember.Expression;
        }

        return expression is MemberExpression member
            && (IsSameMember(member.Member, mappedProperty.PropertyInfo)
                || IsSameMember(member.Member, mappedProperty.FieldInfo))
            && member.Expression is not null
            && ReferenceEquals(member.Expression, parameter);
    }

    private static bool IsSameMember(MemberInfo actual, MemberInfo? expected) =>
        expected is not null
        && actual.Module.Equals(expected.Module)
        && actual.MetadataToken == expected.MetadataToken;

    private static bool IsCapturedProperty<T>(
        Expression expression,
        string propertyName,
        T expectedReceiver)
        where T : class
    {
        expression = StripConvert(expression);
        if (expression is not MemberExpression member
            || member.Member.DeclaringType != typeof(T)
            || member.Member.Name != propertyName
            || member.Expression is null)
            return false;

        var receiver = StripConvert(member.Expression);
        return receiver.Type == typeof(T)
            && receiver is MemberExpression { Expression: not null } captured
            && StripConvert(captured.Expression) is ConstantExpression { Value: AppDbContext context }
            && ReferenceEquals(ReadCapturedValue(captured, context), expectedReceiver);
    }

    private static object? ReadCapturedValue(MemberExpression captured, AppDbContext context) =>
        captured.Member switch
        {
            FieldInfo field => field.GetValue(context),
            PropertyInfo property => property.GetValue(context),
            _ => null,
        };

    private static bool IsNull(Expression expression) =>
        StripConvert(expression) is ConstantExpression { Value: null };

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression
               {
                   NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
               } unary)
            expression = unary.Operand;
        return expression;
    }
}
