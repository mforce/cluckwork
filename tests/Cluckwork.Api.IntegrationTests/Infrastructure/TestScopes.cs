using Cluckwork.Api.Modules.Access.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using OpenIddict.Server;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// A scope only this test host knows. Consent grants a scope only when an MCP tool requires
// it and the person's role meets that tool's policy (#1326), so the scope also gets a probe
// tool every role passes. The tool exists only in this host's container: the walks over
// the API assembly never see it.
internal static class TestScopes
{
    public static IServiceCollection AddTestScope(this IServiceCollection services, string scope)
    {
        var policy = "scope:" + scope;
        services.Configure<OpenIddictServerOptions>(options => options.Scopes.Add(scope));
        services.AddAuthorization(options => options.AddPolicy(policy, p => p.AddRequirements(new OAuthScopeRequirement(scope))));
        services.AddSingleton(McpServerTool.Create(() => scope, new McpServerToolCreateOptions
        {
            Name = "probe_" + scope.Replace('.', '_'),
            Metadata = [new AuthorizeAttribute(), new AuthorizeAttribute(policy)],
        }));
        return services;
    }
}
