using System.Reflection;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Common;
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
