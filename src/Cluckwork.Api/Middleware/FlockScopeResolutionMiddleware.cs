namespace Cluckwork.Api.Middleware;

using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

// #388 — resolves FlockScope per request from UserRoleAssignment rows.
// Runs AFTER TenantResolutionMiddleware (which resolves AccountId from the JWT
// and the acting user) and BEFORE CredentialEpochMiddleware. It touches no
// credential state; its position is pinned by
// CredentialEpochMiddlewareOrderTests.
//
// #612 — skips the DB read for any non-Worker effective role (Owner, Manager,
// Sales, ReadOnly, Denied — role check from the resolved user, no I/O).
// An unresolved user (seeders, one-shot verbs, background jobs) is Unrestricted.
// This read-scope default is separate from FlockScopeGuard, which fails closed
// for unresolved actors on writes (#787).
// 0 assignment rows (grandfathered #73) and any farm-wide row (FlockId=null)
// are Unrestricted too, matching FlockScopeGuard's resolved-worker rules.
public sealed class FlockScopeResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, FlockScope scope, CurrentUserContext user, AppDbContext db)
    {
        // #388 — UseExceptionHandler re-executes the downstream pipeline at
        // /error. Never repeat assignment resolution there: if the original
        // failure was the database, retrying this query makes error rendering
        // fail too and the client receives no mapped ProblemDetails response.
        // A prior successful pass keeps its already-resolved scope; a failure
        // during resolution leaves the default Unrestricted scope, and /error
        // performs no tenant data read.
        if (context.Features.Get<IExceptionHandlerFeature>() is not null)
        {
            await next(context);
            return;
        }

        if (!user.IsResolved)
        {
            // Unresolved user (seeders, one-shot verbs, background jobs): Unrestricted.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        // #612 — only a plain Worker is ever flock-scoped. Owner, Manager,
        // Sales, ReadOnly and Denied all bypass assignment rows entirely —
        // their route permissions are a separate, untouched surface.
        if (Cluckwork.Domain.Accounts.Roles.ResolveEffective(user.Roles)
            != Cluckwork.Domain.Accounts.EffectiveAccountRole.Worker)
        {
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
            // Farm-wide row: grants everything. Unrestricted.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        var flockIds = assignments.Where(a => a.FlockId != null).Select(a => a.FlockId!.Value).ToList();
        scope.Resolve(false, flockIds);
        await next(context);
    }
}
