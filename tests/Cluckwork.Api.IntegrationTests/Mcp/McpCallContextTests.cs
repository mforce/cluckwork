using Cluckwork.Api.Mcp;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// #805 — the identity bridge's throwing cases, with no tools and no MapMcp. Each
// refusal asserts its own reason, so a test cannot pass on a different check firing.
// Rows refer to docs/plans/770-mcp-server/02-guards.md.
public sealed class McpCallContextTests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid FlockId = Guid.NewGuid();
    private static readonly ConnectedApp App = new("client-805", "Field Assistant");

    [Fact]
    public void RestrictedWorkerThroughAConnectedApp_ExposesTheRequestIdentity()
    {
        using var request = new Request();
        request.ResolveAsTheOAuthMiddlewareDoes(unrestricted: false);

        var call = request.Resolve();

        Assert.Equal(AccountId, call.AccountId);
        Assert.Equal(UserId, call.UserId);
        Assert.Equal("worker@farm.test", call.Email);
        Assert.Equal(App, call.ConnectedApp);
    }

    // Row 3b, with the restricted case above: a clause on IsUnrestricted instead of
    // IsResolved refuses one of the two resolved scopes or admits the unresolved one.
    [Fact]
    public void ResolvedUnrestrictedScope_IsAccepted()
    {
        using var request = new Request();
        request.ResolveAsTheOAuthMiddlewareDoes(unrestricted: true);

        Assert.Equal(UserId, request.Resolve().UserId);
    }

    [Fact]
    public void NoHttpContext_Throws()
    {
        using var request = new Request();
        request.ResolveAsTheOAuthMiddlewareDoes(unrestricted: false);
        request.Accessor.HttpContext = null;

        AssertRefused(request.Resolve, "no HttpContext");
    }

    // Row 5 and the causal second-scope case: a helper opens a scope through
    // IServiceScopeFactory and copies the caller's whole identity into it. Every
    // value matches the request's; only reference equality tells the scopes apart.
    [Fact]
    public void SecondScope_WithTheCallersIdentityCopiedIn_Throws()
    {
        using var request = new Request();
        request.ResolveAsTheOAuthMiddlewareDoes(unrestricted: false);

        using var second = request.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope();
        Resolve(second.ServiceProvider, unrestricted: false);

        AssertRefused(() => second.ServiceProvider.GetRequiredService<McpCallContext>(), "not the HTTP request's");
    }

    // Row 1.
    [Fact]
    public void TenantUnresolved_Throws()
    {
        using var request = new Request();
        request.User.Resolve(UserId, "worker@farm.test", ["Worker"], App);
        request.Flocks.Resolve(unrestricted: false, [FlockId]);

        AssertRefused(request.Resolve, "tenant is unresolved");
    }

    // Row 2. Deleting only the IsResolved half stays green, because an unresolved
    // actor's UserId is also empty; the row tests the combined requirement.
    [Fact]
    public void ActorUnresolved_Throws()
    {
        using var request = new Request();
        request.Tenant.Resolve(AccountId);
        request.Flocks.Resolve(unrestricted: false, [FlockId]);

        AssertRefused(request.Resolve, "not a person");
    }

    // Row 4. ResolveSystemActor sets IsResolved with an empty id and no roles.
    [Fact]
    public void SystemActor_Throws()
    {
        using var request = new Request();
        request.Tenant.Resolve(AccountId);
        request.User.ResolveSystemActor("system:cli");
        request.Flocks.Resolve(unrestricted: true, []);

        AssertRefused(request.Resolve, "not a person");
    }

    // Row 3. The unresolved scope is unrestricted, the state the hazard widens to.
    [Fact]
    public void FlockScopeUnresolved_Throws()
    {
        using var request = new Request();
        request.Tenant.Resolve(AccountId);
        request.User.Resolve(UserId, "worker@farm.test", ["Worker"], App);

        AssertRefused(request.Resolve, "flock scope is unresolved");
    }

    [Fact]
    public void SessionJwtCaller_Throws()
    {
        using var request = new Request();
        request.Tenant.Resolve(AccountId);
        request.User.Resolve(UserId, "worker@farm.test", ["Worker"], connectedApp: null);
        request.Flocks.Resolve(unrestricted: false, [FlockId]);

        AssertRefused(request.Resolve, "connected app");
    }

    private static void AssertRefused(Func<McpCallContext> resolve, string reason)
    {
        var refusal = Assert.Throws<InvalidOperationException>(resolve);
        Assert.Contains(reason, refusal.Message);
    }

    private static void Resolve(IServiceProvider services, bool unrestricted)
    {
        services.GetRequiredService<TenantContext>().Resolve(AccountId);
        services.GetRequiredService<CurrentUserContext>().Resolve(UserId, "worker@farm.test", ["Worker"], App);
        services.GetRequiredService<FlockScope>().Resolve(unrestricted, unrestricted ? [] : [FlockId]);
    }

    // One HTTP request: the identity primitives registered as the API registers them,
    // and an HttpContext whose RequestServices is the request scope.
    private sealed class Request : IDisposable
    {
        private readonly ServiceProvider _root;

        public Request()
        {
            var services = new ServiceCollection();
            services.AddScoped<TenantContext>();
            services.AddScoped<CurrentUserContext>();
            services.AddScoped<FlockScope>();
            services.AddHttpContextAccessor();
            services.AddScoped<McpCallContext>();
            _root = services.BuildServiceProvider(validateScopes: true);
            Scope = _root.CreateScope();
            Accessor = _root.GetRequiredService<IHttpContextAccessor>();
            Accessor.HttpContext = new DefaultHttpContext { RequestServices = Scope.ServiceProvider };
        }

        public IServiceScope Scope { get; }
        public IHttpContextAccessor Accessor { get; }
        public TenantContext Tenant => Scope.ServiceProvider.GetRequiredService<TenantContext>();
        public CurrentUserContext User => Scope.ServiceProvider.GetRequiredService<CurrentUserContext>();
        public FlockScope Flocks => Scope.ServiceProvider.GetRequiredService<FlockScope>();

        public void ResolveAsTheOAuthMiddlewareDoes(bool unrestricted) => McpCallContextTests.Resolve(Scope.ServiceProvider, unrestricted);

        public McpCallContext Resolve() => Scope.ServiceProvider.GetRequiredService<McpCallContext>();

        public void Dispose()
        {
            Accessor.HttpContext = null;
            Scope.Dispose();
            _root.Dispose();
        }
    }
}
