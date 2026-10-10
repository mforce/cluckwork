using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;

namespace Cluckwork.Api.Mcp;

// #805 — the only object an MCP tool may learn "who is calling" from. Constructing
// it IS the check: there is no unresolved instance to test for.
//
// The hazard it closes: the MCP SDK can run a tool in a fresh DI scope
// (McpServerOptions.ScopeRequests, true unless SessionMode is Stateless). Every
// identity primitive is scoped and populated by middleware in the HTTP request's
// scope, so in a fresh one writes throw (IAuditWriter, #500) but reads go silently
// wrong: the tenant filter matches Guid.Empty and an unresolved FlockScope is
// unrestricted, widening a #388-narrowed Worker to the whole farm.
//
// What this does not close: a tool can still reach a second scope through a
// helper (guarded by McpSecondaryScopeTests) or through detached background work
// that adds no dependency edge at all. That residue is recorded in
// docs/plans/770-mcp-server/02-guards.md, not claimed away here.
public sealed class McpCallContext
{
    public Guid AccountId { get; }
    public Guid UserId { get; }
    public string Email { get; }
    public ConnectedApp ConnectedApp { get; }

    public McpCallContext(
        IHttpContextAccessor httpContextAccessor,
        TenantContext tenant,
        CurrentUserContext user,
        FlockScope flockScope)
    {
        var httpContext = httpContextAccessor.HttpContext
            ?? throw Refused("no HttpContext is present");

        // The only check that detects a SessionMode change: a fresh scope's
        // TenantContext is a different instance even when it names the same farm.
        if (!ReferenceEquals(tenant, httpContext.RequestServices.GetRequiredService<TenantContext>()))
            throw Refused("the injected TenantContext is not the HTTP request's");

        if (!tenant.IsResolved)
            throw Refused("the tenant is unresolved");

        // UserId is checked too: ResolveSystemActor resolves with an empty id and no
        // roles, which FlockScopeGuard reads as an account-wide Worker.
        if (!user.IsResolved || user.UserId == Guid.Empty)
            throw Refused("the caller is not a person");

        // IsResolved, never IsUnrestricted: an unresolved scope is unrestricted.
        if (!flockScope.IsResolved)
            throw Refused("the flock scope is unresolved");

        // Only an OAuth access token carries client_id. A session JWT reaching a tool
        // has lost its app attribution; AcceptOAuthTokens refuses it at the route.
        ConnectedApp = user.ConnectedApp
            ?? throw Refused("the caller did not arrive through a connected app");

        AccountId = tenant.AccountId;
        UserId = user.UserId;
        Email = user.Email;
    }

    private static InvalidOperationException Refused(string reason) =>
        new($"An MCP tool cannot run: {reason}.");
}
