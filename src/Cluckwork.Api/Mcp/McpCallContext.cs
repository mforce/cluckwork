using Cluckwork.Domain.Auditing;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Persistence;

namespace Cluckwork.Api.Mcp;

// The only object an MCP tool may learn "who is calling" from (#805). Building it
// IS the check: the constructor throws unless DI resolved it in the HTTP request's
// own scope after the middleware populated tenant, actor and flock scope. In a
// fresh scope the EF filters match AccountId == Guid.Empty and an unresolved
// FlockScope is unrestricted, so a Worker narrowed by #388 would read the whole
// farm (docs/plans/770-mcp-server/01-design.md, guards 1-5).
public sealed class McpCallContext
{
    public Guid AccountId { get; }
    public Guid UserId { get; }
    public string Email { get; }
    public EffectiveAccountRole EffectiveRole { get; }
    public bool IsFlockRestricted { get; }
    public IReadOnlyCollection<Guid> AssignedFlockIds { get; }
    public ConnectedApp ConnectedApp { get; }

    public McpCallContext(
        IHttpContextAccessor httpContextAccessor, TenantContext tenant, CurrentUserContext user, FlockScope flockScope)
    {
        var httpContext = httpContextAccessor.HttpContext ?? throw Refused("no HTTP request is in flight");

        // The only check that notices a SessionMode change: a per-call scope still
        // sees the request's HttpContext, but holds its own, unpopulated TenantContext.
        if (!ReferenceEquals(tenant, httpContext.RequestServices?.GetService<TenantContext>()))
            throw Refused("it was resolved outside the HTTP request's DI scope");

        if (!tenant.IsResolved)
            throw Refused("the tenant is unresolved");
        if (!user.IsResolved)
            throw Refused("the actor is unresolved");

        // ResolveSystemActor resolves with an empty id and no roles. That reads as a
        // Worker with zero assignment rows, which FlockScopeGuard treats as account-wide.
        if (user.UserId == Guid.Empty)
            throw Refused("the actor is a system actor");

        // IsResolved, never IsUnrestricted: an unresolved FlockScope defaults to unrestricted.
        if (!flockScope.IsResolved)
            throw Refused("the flock scope is unresolved");

        // Only an OAuth access token carries client_id. The route refuses session
        // JWTs; this is the second line, and AuditWriter needs it for attribution.
        ConnectedApp = user.ConnectedApp ?? throw Refused("the caller is not a connected app");

        AccountId = tenant.AccountId;
        UserId = user.UserId;
        Email = user.Email;
        EffectiveRole = Roles.ResolveEffective(user.Roles);
        IsFlockRestricted = !flockScope.IsUnrestricted;
        AssignedFlockIds = flockScope.AssignedFlockIds;
    }

    private static InvalidOperationException Refused(string reason) =>
        new($"McpCallContext refused: {reason}.");
}
