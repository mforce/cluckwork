using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Http;
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

    private static readonly ReviewedRequestReader Bridge =
        new(typeof(McpCallContext), "reads RequestServices only to compare its TenantContext with the injected one");

    // Each type below was read: it uses the request only as listed and never touches
    // RequestServices.
    private static readonly ReviewedRequestReader[] ReviewedReaders =
    [
        Bridge,
        new(typeof(IdentityProvider), "reads the caller's IP address for security-event log lines"),
        new(typeof(AuthSecurityEventLogger), "reads the caller's IP address for security-event log lines"),
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
            Reviewed, ReviewedReaders);

        var failures = report.Findings
            .Concat(Reviewed.Except(report.FactoriesReached).Select(r => $"stale review: {r.Service.Name} by {r.RegisteredBy.Name}"))
            .Concat(ReviewedReaders.Except(report.ReadersReached).Select(r => $"stale review: {r.Consumer.Name} reads the request"))
            .ToList();
        Assert.True(failures.Count == 0, "secondary-scope walk failed:\n" + string.Join("\n", failures));
    }

    // The hazard on the real EF filters. A Worker is assigned one flock of two. A helper's
    // second scope, with the account copied in, reads both, because its FlockScope is
    // unresolved and so unrestricted; the bridge refuses to exist there.
    [Fact]
    public async Task SecondScope_WidensAWorkersFlocks_AndMcpCallContextRefusesIt()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"mcp-{Guid.NewGuid():N}@test.local");
        var farmId = Guid.NewGuid();
        var assigned = await factory.SeedFlockAsync(accountId, farmId);
        var unassigned = await factory.SeedFlockAsync(accountId, farmId);

        using var request = factory.Services.CreateScope();
        var services = request.ServiceProvider;
        services.GetRequiredService<TenantContext>().Resolve(accountId);
        services.GetRequiredService<CurrentUserContext>()
            .Resolve(Guid.NewGuid(), "worker@test.local", ["Worker"], new ConnectedApp("client-805", "Field Assistant"));
        services.GetRequiredService<FlockScope>().Resolve(unrestricted: false, [assigned]);
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { RequestServices = services };
        try
        {
            var call = services.GetRequiredService<McpCallContext>();
            var requestFlocks = await FlockIdsAsync(services);

            using var second = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            second.ServiceProvider.GetRequiredService<TenantContext>().Resolve(call.AccountId);
            var secondFlocks = await FlockIdsAsync(second.ServiceProvider);
            var refusal = Record.Exception(() => second.ServiceProvider.GetRequiredService<McpCallContext>());

            Assert.Equivalent(
                new
                {
                    Request = new[] { assigned },
                    Second = new[] { assigned, unassigned }.Order().ToArray(),
                    Refusal = "An MCP tool cannot run: the injected TenantContext is not the HTTP request's.",
                },
                new { Request = requestFlocks, Second = secondFlocks, Refusal = refusal?.Message },
                strict: true);
        }
        finally
        {
            accessor.HttpContext = null;
        }
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

    // Mutation (c): a Platform helper outside the MCP namespace takes IHttpContextAccessor
    // and opens a scope from RequestServices. The tool's constructor names no banned type.
    [Fact]
    public void HelperReadingRequestServices_IsAFinding()
    {
        var services = BridgeServices();
        services.AddScoped<RequestServicesHelper>();

        Assert.Empty(McpToolSurface.Findings([typeof(RequestServicesHelperTool)]));
        var finding = Assert.Single(WalkTool<RequestServicesHelperTool>(services, []).Findings);
        Assert.EndsWith("RequestServicesHelper -> IHttpContextAccessor: reaches HttpContext.RequestServices, " +
            "which can open a scope; review the consumer and add a ReviewedRequestReader row", finding);
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
        SecondaryScopeWalk.Walk(services.ToArray(), McpToolSurface.Injections(typeof(TTool)), reviewed, [Bridge]);

    private static async Task<Guid[]> FlockIdsAsync(IServiceProvider services) =>
        [.. (await services.GetRequiredService<AppDbContext>().Flocks.Select(f => f.Id).ToListAsync()).Order()];

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

    private sealed class RequestServicesHelper(IHttpContextAccessor accessor)
    {
        public bool Unrestricted() =>
            accessor.HttpContext!.RequestServices.CreateScope().ServiceProvider.GetRequiredService<FlockScope>().IsUnrestricted;
    }

    [McpServerToolType]
    private sealed class RequestServicesHelperTool(McpCallContext call, RequestServicesHelper helper)
    {
        [McpServerTool]
        public string Read() => $"{call.UserId} {helper.Unrestricted()}";
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
