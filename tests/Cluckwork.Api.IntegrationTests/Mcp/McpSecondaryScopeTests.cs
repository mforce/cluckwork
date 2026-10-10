using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #805 row 7b — a tool's data access stays in the request scope, judged over the real
// host's registrations. CW1004 lets a tool reach only module contracts and Platform
// types, so the walk roots at every registered contract as well as every tool: a tool
// added later inherits a contract graph already proven clean.
[Collection(IntegrationCollection.Name)]
public sealed class McpSecondaryScopeTests(CluckworkWebApplicationFactory factory)
{
    // Each factory below was read: it resolves from the provider it is handed, in the
    // same scope, and keeps no provider afterwards.
    private static readonly ReviewedFactory[] Reviewed =
    [
        new(typeof(ICurrentUser), typeof(CluckworkIdentityServiceCollectionExtensions),
            "forwards to the same scope's CurrentUserContext"),
        new(typeof(IClaimOnceStore), typeof(SharedStateRegistration),
            "builds the singleton store from singletons, in-process or Redis-backed"),
        new(typeof(DbContextOptions<AppDbContext>), typeof(EntityFrameworkServiceCollectionExtensions),
            "AddDbContext; its options delegate resolves TenantStampInterceptor from the same scope"),
        new(typeof(IOpenIddictTokenManager), typeof(OpenIddictEntityFrameworkCoreBuilder),
            "OpenIddict forwards to its typed token manager in the same scope"),
        new(typeof(IOpenIddictAuthorizationManager), typeof(OpenIddictEntityFrameworkCoreBuilder),
            "OpenIddict forwards to its typed authorization manager in the same scope"),
    ];

    [Fact]
    public void RealHost_NoToolOrContractReachesASecondaryScope()
    {
        ServiceDescriptor[] services = [];
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(collection => services = collection.ToArray()));
        _ = host.Services;

        // The suffix rule ModuleContracts.OwnerOf applies for CW1004.
        var contracts = services
            .Where(d => !d.IsKeyedService && d.ServiceType.Namespace?.EndsWith(".Contracts", StringComparison.Ordinal) == true
                && d.ServiceType.Assembly.GetName().Name!.StartsWith("Cluckwork.", StringComparison.Ordinal))
            .Select(d => d.ServiceType).Distinct().ToList();
        Assert.Contains(typeof(IFlockModule), contracts);
        Assert.Contains(typeof(IEggOperationsModule), contracts);

        var tools = McpToolSurface.ToolTypes(typeof(McpCallContext).Assembly.GetTypes()).SelectMany(McpToolSurface.Injections);
        var report = SecondaryScopeWalk.Walk(services,
            contracts.Select(c => ("contract", c))
                .Concat(tools)
                .Append(("bridge", typeof(McpCallContext))),
            Reviewed);

        var failures = report.Findings
            .Concat(Reviewed.Except(report.ReviewedReached).Select(r => $"stale review: {r.Service.Name} by {r.RegisteredBy.Name}"))
            .ToList();
        Assert.True(failures.Count == 0, "secondary-scope walk failed:\n" + string.Join("\n", failures));
    }

    // Mutation (a): a helper that takes IServiceScopeFactory.
    [Fact]
    public void HelperTakingTheScopeFactory_IsAFinding()
    {
        var services = BridgeServices();
        services.AddScoped<ScopeFactoryHelper>();

        var finding = Assert.Single(WalkTool<ScopeFactoryHelperTool>(services, []).Findings);
        Assert.EndsWith("ScopeFactoryHelper -> IServiceScopeFactory: opens a scope", finding);
    }

    // Mutation (b): the helper's constructor shows only a delegate, so a constructor walk
    // passes it; its factory registration is what the walk refuses.
    [Fact]
    public void HelperRegisteredThroughAFactoryDelegate_IsAFinding()
    {
        var services = BridgeServices();
        RegisterDelegateHelper(services);

        Assert.Empty(McpToolSurface.Findings([typeof(DelegateHelperTool)]));
        var finding = Assert.Single(WalkTool<DelegateHelperTool>(services, []).Findings);
        Assert.Contains("DelegateHelper: registered through a factory delegate", finding);
    }

    [Fact]
    public void ReviewedFactory_ExcusesOnlyTheTypeThatRegistersIt()
    {
        var services = BridgeServices();
        RegisterDelegateHelper(services);

        var here = new ReviewedFactory(typeof(DelegateHelper), typeof(McpSecondaryScopeTests), "test");
        var elsewhere = new ReviewedFactory(typeof(DelegateHelper), typeof(SharedStateRegistration), "test");

        Assert.Empty(WalkTool<DelegateHelperTool>(services, [here]).Findings);
        Assert.Single(WalkTool<DelegateHelperTool>(services, [elsewhere]).Findings);
    }

    private static SecondaryScopeReport WalkTool<TTool>(IServiceCollection services, ReviewedFactory[] reviewed) =>
        SecondaryScopeWalk.Walk(services.ToArray(), McpToolSurface.Injections(typeof(TTool)), reviewed);

    private static void RegisterDelegateHelper(IServiceCollection services) =>
        services.AddScoped(sp => new DelegateHelper(() => sp.CreateScope().ServiceProvider.GetRequiredService<FlockScope>()));

    private static ServiceCollection BridgeServices()
    {
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<CurrentUserContext>();
        services.AddScoped<FlockScope>();
        services.AddHttpContextAccessor();
        services.AddScoped<McpCallContext>();
        return services;
    }

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class McpServerToolTypeAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Method)]
    private sealed class McpServerToolAttribute : Attribute;

    private sealed class ScopeFactoryHelper(IServiceScopeFactory scopes)
    {
        public bool Unrestricted() => scopes.CreateScope().ServiceProvider.GetRequiredService<FlockScope>().IsUnrestricted;
    }

    // The second scope's FlockScope is unresolved, so it is unrestricted.
    private sealed class DelegateHelper(Func<FlockScope> flocks)
    {
        public bool Unrestricted() => flocks().IsUnrestricted;
    }

    [McpServerToolType]
    private sealed class ScopeFactoryHelperTool(McpCallContext call, ScopeFactoryHelper helper)
    {
        [McpServerTool]
        public string Read() => $"{call.UserId} {helper.Unrestricted()}";
    }

    [McpServerToolType]
    private sealed class DelegateHelperTool(McpCallContext call, DelegateHelper helper)
    {
        [McpServerTool]
        public string Read() => $"{call.UserId} {helper.Unrestricted()}";
    }
}
