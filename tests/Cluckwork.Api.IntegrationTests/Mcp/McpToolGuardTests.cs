using Cluckwork.Api.IntegrationTests.Mcp.Fixtures;
using Cluckwork.Api.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// Guards 6, 7 and 7b of docs/plans/770-mcp-server/02-guards.md. The real tree has no
// tool yet (#806 adds the first), so each walk is proved red here against fixture tools
// carrying the attribute names the SDK uses.
public sealed class McpToolGuardTests
{
    [Fact]
    public void ToolDiscovery_MatchesTheSdkAttributeName()
    {
        Assert.Equal(
            [typeof(AmbientConstructorTool), typeof(AmbientMethodTool), typeof(ContextStaticTool), typeof(ContextTool),
             typeof(HelperTool), typeof(NoContextStaticTool), typeof(NoContextTool)],
            McpToolGraph.ToolTypes(typeof(McpToolGuardTests).Assembly).OrderBy(t => t.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryToolTakes_McpCallContext()
    {
        var tools = McpToolGraph.ToolTypes(typeof(McpCallContext).Assembly);

        Assert.Empty(McpToolGraph.MissingCallContext(tools));
    }

    [Fact]
    public void EveryToolTakes_McpCallContext_GoesRed()
    {
        Assert.Equal(
            [
                $"{typeof(NoContextTool).FullName}.Run can run without McpCallContext",
                $"{typeof(NoContextStaticTool).FullName}.Run can run without McpCallContext",
            ],
            McpToolGraph.MissingCallContext(
                [typeof(ContextTool), typeof(ContextStaticTool), typeof(NoContextTool), typeof(NoContextStaticTool)]));
    }

    [Fact]
    public void NoToolInjects_AmbientIdentityOrServiceLocator()
    {
        var tools = McpToolGraph.ToolTypes(typeof(McpCallContext).Assembly);

        Assert.Empty(McpToolGraph.AmbientInjections(tools));
    }

    [Fact]
    public void NoToolInjects_AmbientIdentityOrServiceLocator_GoesRed()
    {
        Assert.Equal(
            [
                $"{typeof(AmbientConstructorTool).FullName}..ctor(tenant) injects TenantContext",
                $"{typeof(AmbientMethodTool).FullName}.Run(scopes) injects IServiceScopeFactory",
            ],
            McpToolGraph.AmbientInjections(
                [typeof(ContextTool), typeof(AmbientConstructorTool), typeof(AmbientMethodTool)]));
    }

    // Guard 7b, mutation (a): a helper whose constructor takes the scope factory.
    [Fact]
    public void NoToolReachesASecondaryScope_ThroughAConstructor()
    {
        var services = new ServiceCollection().AddScoped<IScopeHelper, ScopeFactoryHelper>();

        Assert.Equal(
            ["HelperTool -> IScopeHelper -> IServiceScopeFactory reaches IServiceScopeFactory"],
            McpToolGraph.SecondaryScopeRoutes([typeof(HelperTool)], services, new HashSet<string>()));
    }

    // Guard 7b, mutation (b), the one that matters: the constructor exposes only a
    // delegate, so only the registration shows where the scope comes from.
    [Fact]
    public void NoToolReachesASecondaryScope_ThroughAFactoryRegistration()
    {
        var services = new ServiceCollection()
            .AddScoped<IScopeHelper>(sp => new DelegateHelper(() => sp.CreateScope()));

        Assert.Equal(
            ["HelperTool -> IScopeHelper is registered through an unreviewed factory"],
            McpToolGraph.SecondaryScopeRoutes([typeof(HelperTool)], services, new HashSet<string>()));
        Assert.Empty(McpToolGraph.SecondaryScopeRoutes(
            [typeof(HelperTool)], services, new HashSet<string> { typeof(IScopeHelper).FullName! }));
    }

    [Fact]
    public void NoToolReachesASecondaryScope_PassesATypeRegisteredHelper()
    {
        var services = new ServiceCollection().AddScoped<IScopeHelper, PlainHelper>();

        Assert.Empty(McpToolGraph.SecondaryScopeRoutes([typeof(HelperTool)], services, new HashSet<string>()));
    }
}
