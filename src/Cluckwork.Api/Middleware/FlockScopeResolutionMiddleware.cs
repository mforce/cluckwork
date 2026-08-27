namespace Cluckwork.Api.Middleware;

using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #388 — resolves FlockScope per request from UserRoleAssignment rows.
// Runs AFTER TenantResolutionMiddleware (which resolves AccountId from the JWT
// and the acting user) and BEFORE CredentialEpochMiddleware. It touches no
// credential state; its position is pinned by
// CredentialEpochMiddlewareOrderTests.
//
// Skips the DB read for Owner/Manager (role check from the resolved user, no I/O).
// An unresolved user (seeders, one-shot verbs, background jobs) is Unrestricted
// (matches FlockScopeGuard line 70 fail-open behavior).
// 0 assignment rows (grandfathered #73) and any farm-wide row (FlockId=null)
// are Unrestricted too — mirroring FlockScopeGuard lines 80 and 84 exactly.
public sealed class FlockScopeResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, FlockScope scope, CurrentUserContext user, AppDbContext db)
    {
        if (!user.IsResolved)
        {
            // Unresolved user (seeders, one-shot verbs, background jobs): Unrestricted.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        if (user.Roles.Contains(Cluckwork.Domain.Accounts.Roles.Owner)
            || user.Roles.Contains(Cluckwork.Domain.Accounts.Roles.Manager))
        {
            // Owner/Manager: Unrestricted, no DB read.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        var assignments = await db.UserRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == user.UserId)
            .ToListAsync(context.RequestAborted);

        if (assignments.Count == 0)
        {
            // 0 rows: unscoped worker (grandfathered #73). Unrestricted.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        if (assignments.Any(a => a.FlockId == null))
        {
            // Farm-wide row: grants everything (matches FlockScopeGuard line 84). Unrestricted.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        var flockIds = assignments.Where(a => a.FlockId != null).Select(a => a.FlockId!.Value).ToList();
        scope.Resolve(false, flockIds);
        await next(context);
    }
}
