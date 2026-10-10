using Cluckwork.Api.Mcp;
using Cluckwork.Domain.Auditing;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Mcp;

// Guards 1-5 of docs/plans/770-mcp-server/02-guards.md, plus the connected-app case
// (#805, 2026-10-10). No tools and no MapMcp: each test leaves exactly one clause
// failing and everything else as the HTTP pipeline would populate it.
public sealed class McpCallContextTests
{
    private static readonly Guid AccountId = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
    private static readonly Guid FlockA = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");
    private static readonly ConnectedApp App = new("claude-desktop", "Claude");

    [Fact]
    public void PopulatedRequestScope_CarriesTheCaller()
    {
        var call = Build();

        Assert.Equivalent(
            new
            {
                AccountId, UserId, Email = "worker@farm.test", EffectiveRole = EffectiveAccountRole.Worker,
                IsFlockRestricted = true, AssignedFlockIds = new[] { FlockA }, ConnectedApp = App,
            },
            call, strict: true);
    }

    [Fact]
    public void McpCallContext_Throws_WhenNoHttpRequest()
    {
        AssertRefused("no HTTP request is in flight", () => Build(withHttpContext: false));
    }

    // Guard 5. A separate TenantContext resolved to the SAME account is what a fresh
    // per-call scope looks like once something copies the id across. A value-based
    // comparison passes it.
    [Fact]
    public void McpCallContext_Throws_WhenTenantIsNotTheRequestScopes()
    {
        AssertRefused("it was resolved outside the HTTP request's DI scope",
            () => Build(tenant: ResolvedTenant(), requestTenant: ResolvedTenant()));
    }

    [Fact]
    public void McpCallContext_Throws_WhenTenantUnresolved()
    {
        AssertRefused("the tenant is unresolved", () => Build(tenant: new TenantContext()));
    }

    // Guard 2. The reason pins the IsResolved clause itself: without it an unresolved
    // actor is still refused, by the empty-id check, with the system-actor reason.
    [Fact]
    public void McpCallContext_Throws_WhenActorHasNoIdentity()
    {
        AssertRefused("the actor is unresolved", () => Build(user: new CurrentUserContext()));
    }

    // Guard 4.
    [Fact]
    public void McpCallContext_Throws_WhenActorIsSystemActor()
    {
        var system = new CurrentUserContext();
        system.ResolveSystemActor("cli:bootstrap-admin");

        AssertRefused("the actor is a system actor", () => Build(user: system));
    }

    // Guard 3. An unresolved FlockScope reports IsUnrestricted == true.
    [Fact]
    public void McpCallContext_Throws_WhenFlockScopeUnresolved()
    {
        AssertRefused("the flock scope is unresolved", () => Build(flock: new FlockScope()));
    }

    // Guard 3b. The plausible wrong fix reads IsUnrestricted: it refuses this Owner,
    // while guard 3 catches the inverse.
    [Fact]
    public void McpCallContext_ChecksIsResolved_NotIsUnrestricted()
    {
        var owner = new CurrentUserContext();
        owner.Resolve(UserId, "owner@farm.test", [Roles.Owner], App);
        var farmWide = new FlockScope();
        farmWide.Resolve(true, []);

        var call = Build(user: owner, flock: farmWide);

        Assert.Equal((EffectiveAccountRole.Owner, false), (call.EffectiveRole, call.IsFlockRestricted));
    }

    // A session JWT carries no client_id. The route refuses it; this is the second line.
    [Fact]
    public void McpCallContext_Throws_WhenCallerIsNotAConnectedApp()
    {
        var sessionCaller = new CurrentUserContext();
        sessionCaller.Resolve(UserId, "worker@farm.test", []);

        AssertRefused("the caller is not a connected app", () => Build(user: sessionCaller));
    }

    private static void AssertRefused(string reason, Func<McpCallContext> build)
    {
        var refused = Assert.Throws<InvalidOperationException>(build);
        Assert.Equal($"McpCallContext refused: {reason}.", refused.Message);
    }

    private static McpCallContext Build(
        TenantContext? tenant = null, CurrentUserContext? user = null, FlockScope? flock = null,
        TenantContext? requestTenant = null, bool withHttpContext = true)
    {
        tenant ??= ResolvedTenant();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(requestTenant ?? tenant).BuildServiceProvider(),
        };
        return new McpCallContext(
            new Accessor { HttpContext = withHttpContext ? http : null },
            tenant, user ?? ResolvedWorker(), flock ?? RestrictedToFlockA());
    }

    private static TenantContext ResolvedTenant()
    {
        var tenant = new TenantContext();
        tenant.Resolve(AccountId);
        return tenant;
    }

    private static CurrentUserContext ResolvedWorker()
    {
        var user = new CurrentUserContext();
        user.Resolve(UserId, "worker@farm.test", [], App);
        return user;
    }

    private static FlockScope RestrictedToFlockA()
    {
        var scope = new FlockScope();
        scope.Resolve(false, [FlockA]);
        return scope;
    }

    private sealed class Accessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
