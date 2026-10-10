using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Mcp;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// The identity bridge against the real host's registrations (#805).
[Collection(IntegrationCollection.Name)]
public sealed class McpRequestScopeTests(CluckworkWebApplicationFactory factory)
{
    // Factory registrations written in Cluckwork code that the walk reaches, each read and
    // found to open no scope. A new entry needs the same reading, recorded here.
    private static readonly HashSet<string> ReviewedFactories =
    [
        // sp => sp.GetRequiredService<CurrentUserContext>(): the same provider, so the same scope.
        "Cluckwork.Application.Common.ICurrentUser",
        // A singleton built from singletons (multiplexer, logger, TimeProvider); holds no provider.
        "Cluckwork.Infrastructure.SharedState.IClaimOnceStore",
    ];

    // The hazard guard 7b exists for, shown on the real EF filters: a helper that opens a
    // second scope and copies the account id across reads flocks a narrowed Worker was never
    // assigned, because that scope's FlockScope is unresolved and so unrestricted. The bridge
    // refuses to exist there even with the tenant copied.
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
            .Resolve(Guid.NewGuid(), "worker@test.local", [], new ConnectedApp("claude-desktop", "Claude"));
        services.GetRequiredService<FlockScope>().Resolve(false, [assigned]);
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { RequestServices = services };
        try
        {
            var call = services.GetRequiredService<McpCallContext>();
            var requestFlocks = await FlockIdsAsync(services);

            using var second = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            second.ServiceProvider.GetRequiredService<TenantContext>().Resolve(call.AccountId);
            var secondFlocks = await FlockIdsAsync(second.ServiceProvider);
            var refused = Record.Exception(() => second.ServiceProvider.GetRequiredService<McpCallContext>());

            Assert.Equivalent(
                new
                {
                    Request = new[] { assigned },
                    Second = new[] { assigned, unassigned }.Order().ToArray(),
                    Refused = "McpCallContext refused: it was resolved outside the HTTP request's DI scope.",
                },
                new { Request = requestFlocks, Second = secondFlocks, Refused = refused?.Message },
                strict: true);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    // Guard 7b over the real registration model. A tool may reach only module contracts and
    // Platform types (CW1004), so the contracts' graphs are what any tool can reach; walking
    // them keeps this live before the first tool lands, and walks every tool once one does.
    [Fact]
    public void NoToolReachesASecondaryScope()
    {
        IServiceCollection? captured = null;
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => captured = s));
        _ = host.Services;
        var registrations = captured!;

        var contracts = registrations.Select(d => d.ServiceType)
            .Where(t => t.IsInterface && t.Namespace?.EndsWith(".Contracts", StringComparison.Ordinal) == true)
            .Distinct().ToList();
        var tools = McpToolGraph.ToolTypes(typeof(McpCallContext).Assembly);

        Assert.True(contracts.Count >= 20, $"expected the module contracts, found {contracts.Count}");
        Assert.Empty(McpToolGraph.SecondaryScopeRoutes(tools, registrations, ReviewedFactories));
        Assert.Empty(McpToolGraph.Walk(contracts.Select(c => (c.Name, c)), registrations, ReviewedFactories));
    }

    private static async Task<Guid[]> FlockIdsAsync(IServiceProvider services) =>
        [.. (await services.GetRequiredService<AppDbContext>().Flocks.Select(f => f.Id).ToListAsync()).Order()];
}
