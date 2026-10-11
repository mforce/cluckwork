using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #805 rows 6, 7 and the namespace half of 7b. The real tree holds no tool until #806,
// so the fixtures below carry the red mutations.
public sealed class McpToolSurfaceTests
{
    private static readonly Type[] ApiTypes = typeof(McpCallContext).Assembly.GetTypes();

    [Fact]
    public void RealTree_EveryToolTakesMcpCallContext_AndInjectsNoAmbientIdentity() =>
        AssertNone(McpToolSurface.Findings(ApiTypes));

    [Fact]
    public void RealTree_EveryToolCarriesARolePolicyAndAScopePolicy() =>
        AssertNone(McpToolSurface.GateFindings(ApiTypes));

    [Fact]
    public void RealTree_NoTypeUnderTheMcpNamespace_DeclaresAScopeOpener()
    {
        var mcpTypes = ApiTypes.Where(t => McpToolSurface.InNamespace(t, "Cluckwork.Api.Mcp")).ToList();

        Assert.Contains(typeof(McpCallContext), mcpTypes);
        AssertNone(McpToolSurface.ScopeOpenerDeclarations(mcpTypes));
    }

    // #807, row 31: a read tool's reply says whether it is complete and where to continue.
    [Fact]
    public void RealTree_EveryReadToolReturnsAPage()
    {
        var reads = ReadTools().ToList();

        Assert.True(reads.Count >= 5, $"the walk found {reads.Count} read tools");
        AssertNone([.. reads.Where(m => !(m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                && m.ReturnType.GetGenericArguments()[0].IsGenericType
                && m.ReturnType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(McpPage<>)))
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name} returns {m.ReturnType.Name}; return Task<McpPage<T>>")]);
    }

    // #792: phase 1 tools return no farm-entered free text. Every text field a read tool
    // returns is listed here with why it is not one, so a new note or reason field fails.
    private static readonly string[] ReplyText =
    [
        "DailyEntryRow.FlockName", // a flock's name: the label tools address flocks by, set by Owner or Manager
        "DailyEntryRow.Status", // enum
        "EggLotMovementRow.MovementType", // enum
        "EggLotMovementRow.ReferenceType", // the source record's type name, set by code
        "EggLotRow.Grade", // a grade's name, set by Owner or Manager
        "FlockRow.Name",
        "FlockRow.Status", // enum
        "GradeCount.Grade",
        "StockRow.Grade",
    ];

    [Fact]
    public void RealTree_ReadToolRepliesReturnOnlyReviewedText() =>
        Assert.Equal(ReplyText.Order(), ReadTools().SelectMany(m => TextFields(m.ReturnType, m.Name, [])).Distinct().Order());

    // The walk reaches text a serializer would emit from any namespace, inside a collection
    // or array, and refuses a reply shape it cannot see into.
    [Theory]
    [InlineData(typeof(NestedContractRow), "DailyEntryDetails.AdjustReason")]
    [InlineData(typeof(NestedContractRow), "DailyEntryDetails.VoidReason")]
    [InlineData(typeof(TextCollectionRow), "TextCollectionRow.Notes")]
    [InlineData(typeof(TextCollectionRow), "TextCollectionRow.Tags")]
    [InlineData(typeof(OpaqueRow), "OpaqueRow.Extra is opaque")]
    [InlineData(typeof(OpaqueRow), "OpaqueRow.Raw is opaque")]
    public void TextWalk_ReachesNestedCollectionAndOpaqueText(Type row, string field) =>
        Assert.Contains(field, TextFields(typeof(Task<>).MakeGenericType(typeof(McpPage<>).MakeGenericType(row)), "Fixture", []));

    // A conditionally ignored property still serializes when it holds a value; only an
    // always-ignored one never does. The walk agrees with the serializer on both.
    [Fact]
    public void TextWalk_FollowsTheSerializer_OnIgnoredProperties()
    {
        var json = JsonSerializer.Serialize(new IgnoredRow("farm-entered note", "never sent"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var fields = TextFields(typeof(IgnoredRow), "Fixture", []);

        Assert.Equal("""{"reason":"farm-entered note"}""", json);
        Assert.Equal(["IgnoredRow.Reason"], fields);
    }

    private static IEnumerable<MethodInfo> ReadTools() =>
        McpToolSurface.ToolTypes(ApiTypes).SelectMany(McpToolSurface.ToolMethods)
            .Where(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()!.ReadOnly);

    // Follows a reply the way System.Text.Json serializes it: Task<T> to T, a nullable to its
    // value, a collection or array to its element, any other type to its public properties.
    // A text field is keyed by the property holding it.
    private static IReadOnlyList<string> TextFields(Type type, string holder, HashSet<Type> seen)
    {
        if (type == typeof(string) || type == typeof(char))
            return [holder];
        if (Nullable.GetUnderlyingType(type) is { } value)
            return TextFields(value, holder, seen);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return TextFields(type.GetGenericArguments()[0], holder, seen);
        if (ElementType(type) is { } element)
            return TextFields(element, holder, seen);
        if (type == typeof(object) || type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type) || type.IsInterface)
            return [$"{holder} is opaque"];
        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateOnly)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeOnly) || type == typeof(TimeSpan))
            return [];
        if (!seen.Add(type))
            return [];
        return [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                && p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
            .SelectMany(p => TextFields(p.PropertyType, $"{type.Name}.{p.Name}", seen))];
    }

    private static Type? ElementType(Type type) =>
        type.IsArray ? type.GetElementType()
        : type == typeof(string) ? null
        : new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];

    private sealed record NestedContractRow(Guid Id, DailyEntryDetails Entry);

    private sealed record TextCollectionRow(Guid Id, IReadOnlyList<string> Notes, string[] Tags);

    private sealed record OpaqueRow(Guid Id, object Extra, JsonElement Raw);

    private sealed record IgnoredRow(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
        [property: JsonIgnore] string Hidden);

    [Fact]
    public void RealTree_EveryWriteToolTakesAnIdempotencyKey()
    {
        var writes = McpToolSurface.ToolTypes(ApiTypes).SelectMany(McpToolSurface.ToolMethods)
            .Where(m => m.Name == nameof(DailyEntryTools.RecordAsync));

        Assert.Single(writes);
        AssertNone(McpToolSurface.WriteToolFindings(ApiTypes));
    }

    [Fact]
    public void WriteToolWithoutARequiredKey_IsAFinding_AndAReadOrAKeyedWriteIsNot() =>
        Assert.Equal(
            [
                $"{typeof(KeylessWriteTool).FullName}.Write writes without a required string idempotencyKey parameter",
                $"{typeof(OptionalKeyWriteTool).FullName}.Write writes without a required string idempotencyKey parameter",
            ],
            McpToolSurface.WriteToolFindings(
                [typeof(KeylessWriteTool), typeof(OptionalKeyWriteTool), typeof(KeyedWriteTool), typeof(KeylessReadTool)]));

    [Fact]
    public void ToolsTakingTheContext_HaveNoFindings() =>
        Assert.Empty(McpToolSurface.Findings([typeof(ConstructorTool), typeof(StaticTool)]));

    // Row 6.
    [Theory]
    [InlineData(typeof(BareTool))]
    [InlineData(typeof(HalfTool))]
    public void ToolRunningWithoutTheContext_IsAFinding(Type tool)
    {
        var finding = Assert.Single(McpToolSurface.Findings([tool]));
        Assert.Contains("runs without McpCallContext", finding);
    }

    // Row 7, through a constructor and through a tool-method parameter.
    [Theory]
    [InlineData(typeof(TenantContext))]
    [InlineData(typeof(CurrentUserContext))]
    [InlineData(typeof(ICurrentUser))]
    [InlineData(typeof(FlockScope))]
    [InlineData(typeof(AppDbContext))]
    [InlineData(typeof(DbContext))]
    [InlineData(typeof(IServiceProvider))]
    [InlineData(typeof(IServiceScopeFactory))]
    [InlineData(typeof(Func<IServiceProvider>))]
    [InlineData(typeof(HttpContext))]
    [InlineData(typeof(IHttpContextAccessor))]
    public void ToolInjectingAmbientIdentityOrAServiceLocator_IsAFinding(Type injected)
    {
        foreach (var tool in new[] { typeof(ConstructorInjects<>), typeof(MethodInjects<>) })
        {
            var finding = Assert.Single(McpToolSurface.Findings([tool.MakeGenericType(injected)]));
            Assert.Contains($"injects {injected.Name}", finding);
        }
    }

    [Fact]
    public void GatedTools_HaveNoGateFindings() =>
        Assert.Empty(McpToolSurface.GateFindings([typeof(GatedTool), typeof(TypeGatedTool)]));

    // The floor for the gate fixtures: the walk reaches every tool method in them,
    // including one a tool type inherits. McpEndpointTests holds the real tree to the
    // tools the SDK actually registers.
    [Fact]
    public void GateWalk_ReachesEveryFixtureToolMethod()
    {
        Type[] fixtures = [typeof(GatedTool), typeof(TypeGatedTool), typeof(RoleOnlyTool),
            typeof(ScopeOnlyTool), typeof(AnonymousTool), typeof(InheritingTool)];

        var methods = McpToolSurface.ToolTypes(fixtures).SelectMany(McpToolSurface.ToolMethods)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}").Order();

        Assert.Equal(["AnonymousTool.Read", "GatedTool.Read", "InheritingTool.Read", "RoleOnlyTool.Read",
            "ScopeOnlyTool.Read", "TypeGatedTool.Write", "UngatedBase.Leak"], methods);
    }

    // The SDK serves a public tool method a tool type inherits, under the attributes of
    // the class that declares it, so the gates on InheritingTool do not cover Leak.
    [Fact]
    public void InheritedToolMethod_IsGatedByItsDeclaringType()
    {
        var findings = McpToolSurface.GateFindings([typeof(InheritingTool)]);

        Assert.Contains(findings, f => f.EndsWith(".Leak has no scope policy; add [Authorize(Policy = AuthPolicies.<Scope>Scope)]", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Contains(".Read ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(RoleOnlyTool), "has no scope policy")]
    [InlineData(typeof(ScopeOnlyTool), "has no role policy")]
    [InlineData(typeof(AnonymousTool), "allows anonymous callers")]
    public void ToolMissingAGate_IsAGateFinding(Type tool, string finding) =>
        Assert.Contains(McpToolSurface.GateFindings([tool]), f => f.Contains(finding));

    [Fact]
    public void ScopeFactoryHelper_IsADeclarationFinding()
    {
        var findings = McpToolSurface.ScopeOpenerDeclarations(WithNested(typeof(ScopeFactoryHelper)));
        Assert.Contains(findings, f => f.Contains(".ctor(scopes) declares IServiceScopeFactory"));
    }

    [Fact]
    public void RegistrationLambdaTakingTheProvider_IsADeclarationFinding()
    {
        var findings = McpToolSurface.ScopeOpenerDeclarations(WithNested(typeof(FactoryRegistration)));
        Assert.Contains(findings, f => f.Contains("declares IServiceProvider"));
    }

    // xUnit truncates each item of a failing Assert.Empty; a finding is only useful whole.
    private static void AssertNone(IReadOnlyList<string> findings) =>
        Assert.True(findings.Count == 0, string.Join("\n", findings));

    private static IEnumerable<Type> WithNested(Type type) =>
        type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested).Prepend(type);

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class McpServerToolTypeAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Method)]
    private sealed class McpServerToolAttribute : Attribute;

    [McpServerToolType]
    private sealed class ConstructorTool(McpCallContext call, TimeProvider clock)
    {
        [McpServerTool]
        public string Read() => $"{call.UserId} {clock.GetUtcNow()}";
    }

    [McpServerToolType]
    private static class StaticTool
    {
        [McpServerTool]
        public static string Read(McpCallContext call) => call.Email;
    }

    [McpServerToolType]
    private sealed class BareTool
    {
        [McpServerTool]
        public string Read() => "";
    }

    // The constructor takes the context, but a static tool method never constructs it.
    [McpServerToolType]
    private sealed class HalfTool(McpCallContext call)
    {
        [McpServerTool]
        public static string Read() => "";

        public Guid Caller => call.UserId;
    }

    [McpServerToolType]
    private sealed class ConstructorInjects<T>(McpCallContext call, T injected)
    {
        [McpServerTool]
        public string Read() => $"{call.UserId} {injected}";
    }

    [McpServerToolType]
    private sealed class MethodInjects<T>(McpCallContext call)
    {
        [McpServerTool]
        public string Read(T injected) => $"{call.UserId} {injected}";
    }

    [McpServerToolType]
    private sealed class GatedTool(McpCallContext call)
    {
        [McpServerTool, Authorize, Authorize(Policy = AuthPolicies.FarmReadScope)]
        public string Read() => call.Email;
    }

    [McpServerToolType, Authorize(Policy = AuthPolicies.ProductionWrite), Authorize(Policy = AuthPolicies.DailyEntriesWriteScope)]
    private sealed class TypeGatedTool(McpCallContext call)
    {
        [McpServerTool]
        public string Write() => call.Email;
    }

    [McpServerToolType]
    private sealed class RoleOnlyTool(McpCallContext call)
    {
        [McpServerTool, Authorize(Policy = AuthPolicies.AdminOnly)]
        public string Read() => call.Email;
    }

    [McpServerToolType]
    private sealed class ScopeOnlyTool(McpCallContext call)
    {
        [McpServerTool, Authorize(Policy = AuthPolicies.FarmReadScope)]
        public string Read() => call.Email;
    }

    [McpServerToolType, Authorize, Authorize(Policy = AuthPolicies.FarmReadScope)]
    private sealed class AnonymousTool(McpCallContext call)
    {
        [McpServerTool, AllowAnonymous]
        public string Read() => call.Email;
    }

    private class UngatedBase
    {
        [McpServerTool]
        public string Leak() => "";
    }

    [McpServerToolType, Authorize, Authorize(Policy = AuthPolicies.FarmReadScope)]
    private sealed class InheritingTool(McpCallContext call) : UngatedBase
    {
        [McpServerTool]
        public string Read() => call.Email;
    }

    [McpServerToolType]
    private sealed class KeylessWriteTool(McpCallContext call)
    {
        [ModelContextProtocol.Server.McpServerTool]
        public string Write(int eggs) => $"{call.UserId} {eggs}";
    }

    [McpServerToolType]
    private sealed class OptionalKeyWriteTool(McpCallContext call)
    {
        [ModelContextProtocol.Server.McpServerTool]
        public string Write(int eggs, string idempotencyKey = "shared") => $"{call.UserId} {idempotencyKey} {eggs}";
    }

    [McpServerToolType]
    private sealed class KeyedWriteTool(McpCallContext call)
    {
        [ModelContextProtocol.Server.McpServerTool]
        public string Write(string idempotencyKey, int eggs) => $"{call.UserId} {idempotencyKey} {eggs}";
    }

    [McpServerToolType]
    private sealed class KeylessReadTool(McpCallContext call)
    {
        [ModelContextProtocol.Server.McpServerTool(ReadOnly = true)]
        public string Read() => call.Email;
    }

    private sealed class ScopeFactoryHelper(IServiceScopeFactory scopes)
    {
        public IServiceScope Open() => scopes.CreateScope();
    }

    private static class FactoryRegistration
    {
        public static void Register(IServiceCollection services) =>
            services.AddScoped(sp => new Func<FlockScope>(() => sp.CreateScope().ServiceProvider.GetRequiredService<FlockScope>()));
    }
}
