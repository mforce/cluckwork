using Cluckwork.Api.Mcp;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp.Fixtures;

// Stand-ins for the MCP SDK's attributes. McpToolGraph matches the simple name.
[AttributeUsage(AttributeTargets.Class)]
internal sealed class McpServerToolTypeAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class McpServerToolAttribute : Attribute;

[McpServerToolType]
internal sealed class ContextTool(McpCallContext call, IFlockLookup flocks)
{
    [McpServerTool]
    public string Run(Guid? flockId = null) => $"{call.AccountId}:{flocks}:{flockId}";
}

[McpServerToolType]
internal static class ContextStaticTool
{
    [McpServerTool]
    public static string Run(McpCallContext call) => call.Email;
}

[McpServerToolType]
internal sealed class NoContextTool(IFlockLookup flocks)
{
    [McpServerTool]
    public string Run() => $"{flocks}";
}

[McpServerToolType]
internal static class NoContextStaticTool
{
    [McpServerTool]
    public static string Run(Guid flockId) => $"{flockId}";
}

[McpServerToolType]
internal sealed class AmbientConstructorTool(McpCallContext call, TenantContext tenant)
{
    [McpServerTool]
    public string Run() => $"{call.AccountId}:{tenant.AccountId}";
}

[McpServerToolType]
internal sealed class AmbientMethodTool(McpCallContext call)
{
    [McpServerTool]
    public string Run(Func<IServiceScopeFactory> scopes) => $"{call.AccountId}:{scopes}";
}

[McpServerToolType]
internal sealed class HelperTool(McpCallContext call, IScopeHelper helper)
{
    [McpServerTool]
    public string Run() => $"{call.AccountId}:{helper}";
}

internal interface IScopeHelper;

internal sealed class ScopeFactoryHelper(IServiceScopeFactory scopes) : IScopeHelper
{
    public override string ToString() => $"{scopes}";
}

internal sealed class DelegateHelper(Func<IServiceScope> openScope) : IScopeHelper
{
    public override string ToString() => $"{openScope}";
}

internal sealed class PlainHelper : IScopeHelper;
