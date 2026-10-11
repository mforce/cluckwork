using System.Reflection;
using System.Security.Claims;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #807, guard rows 13 to 15. Every tool names the REST route it mirrors, the name resolves,
// and the tool admits no role the route denies. Both sides are evaluated through the live
// authorization service, so tightening a route without touching Mcp/ turns this red.
[Collection(IntegrationCollection.Name)]
public sealed class McpRouteParityTests(CluckworkWebApplicationFactory factory)
{
    // The effective roles AuthPolicies distinguishes. A Worker carries no role claim.
    private static readonly (string Name, ClaimsPrincipal Principal)[] Callers =
        [.. new[] { Roles.Owner, Roles.Manager, Roles.Sales, Roles.ReadOnly, null }
            .Select(role => (role ?? "Worker", new ClaimsPrincipal(new ClaimsIdentity(
                role is null ? [new Claim("sub", "parity")] : [new Claim("sub", "parity"), new Claim("role", role)],
                "parity", "sub", "role"))))];

    [Fact]
    public async Task RealTree_EveryToolMirrorsARoute_AndAdmitsNoRoleItDenies()
    {
        var tools = McpToolSurface.ToolTypes(typeof(McpCallContext).Assembly.GetTypes()).ToList();

        Assert.True(tools.SelectMany(McpToolSurface.ToolMethods).Count() >= 5, "the walk found fewer tools than #807 shipped");
        AssertNone(await FindingsAsync(tools));
    }

    [Theory]
    [InlineData(typeof(NoMirrorTool), "declares no [MirrorsRoute]")]
    [InlineData(typeof(UnknownRouteTool), "mirrors 'NoSuchRoute', which no endpoint is named")]
    [InlineData(typeof(WiderThanRouteTool), "admits Worker, which RecordEggLotMovement denies")]
    public async Task ToolBreakingParity_IsAFinding(Type tool, string finding) =>
        Assert.Contains(await FindingsAsync([tool]), f => f.Contains(finding, StringComparison.Ordinal));

    [Fact]
    public async Task ToolNarrowerThanItsRoute_IsNoFinding() =>
        Assert.Empty(await FindingsAsync([typeof(NarrowerThanRouteTool)]));

    private async Task<IReadOnlyList<string>> FindingsAsync(IEnumerable<Type> tools)
    {
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<IEndpointNameMetadata>() is not null)
            .ToDictionary(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);

        var findings = new List<string>();
        foreach (var method in McpToolSurface.ToolTypes(tools).SelectMany(McpToolSurface.ToolMethods))
        {
            var site = $"{method.DeclaringType!.FullName}.{method.Name}";
            var mirrored = method.GetCustomAttribute<MirrorsRouteAttribute>()?.RouteName;
            if (mirrored is null)
            {
                findings.Add($"{site} declares no [MirrorsRoute]");
                continue;
            }
            if (!routes.TryGetValue(mirrored, out var route))
            {
                findings.Add($"{site} mirrors '{mirrored}', which no endpoint is named");
                continue;
            }

            // The tool's role gates, as the SDK reads them; its scope gate has no route twin.
            var toolGates = method.GetCustomAttributes(inherit: true).Concat(method.DeclaringType.GetCustomAttributes(inherit: true))
                .OfType<IAuthorizeData>().Where(a => !McpToolSurface.ScopePolicies.Contains(a.Policy));
            var toolPolicy = await AuthorizationPolicy.CombineAsync(provider, toolGates);
            var routePolicy = await AuthorizationPolicy.CombineAsync(
                provider, route.Metadata.GetOrderedMetadata<IAuthorizeData>(), route.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            if (toolPolicy is null || routePolicy is null)
            {
                findings.Add($"{site} or its route '{mirrored}' carries no role policy");
                continue;
            }

            foreach (var (name, principal) in Callers)
                if ((await authorization.AuthorizeAsync(principal, toolPolicy)).Succeeded
                    && !(await authorization.AuthorizeAsync(principal, routePolicy)).Succeeded)
                    findings.Add($"{site} admits {name}, which {mirrored} denies");
        }

        return findings;
    }

    private static void AssertNone(IReadOnlyList<string> findings) =>
        Assert.True(findings.Count == 0, string.Join("\n", findings));

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class McpServerToolTypeAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Method)]
    private sealed class McpServerToolAttribute : Attribute;

    [McpServerToolType]
    private sealed class NoMirrorTool
    {
        [McpServerTool, Authorize, Authorize(Policy = AuthPolicies.FarmReadScope)]
        public string Read() => "";
    }

    [McpServerToolType]
    private sealed class UnknownRouteTool
    {
        [McpServerTool, Authorize, Authorize(Policy = AuthPolicies.FarmReadScope), MirrorsRoute("NoSuchRoute")]
        public string Read() => "";
    }

    // RecordEggLotMovement is AdminOnly; the default policy also admits a Worker.
    [McpServerToolType]
    private sealed class WiderThanRouteTool
    {
        [McpServerTool, Authorize, Authorize(Policy = AuthPolicies.FarmReadScope), MirrorsRoute("RecordEggLotMovement")]
        public string Write() => "";
    }

    [McpServerToolType]
    private sealed class NarrowerThanRouteTool
    {
        [McpServerTool, Authorize(Policy = AuthPolicies.AdminOnly), Authorize(Policy = AuthPolicies.FarmReadScope), MirrorsRoute("ListFlocks")]
        public string Read() => "";
    }
}
