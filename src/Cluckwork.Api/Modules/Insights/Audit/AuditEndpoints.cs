using Cluckwork.Application.Modules.Insights.Contracts;
using Cluckwork.Infrastructure.Persistence;

namespace Cluckwork.Api.Modules.Insights.Audit;

// #93 — read-only audit viewer. There is deliberately NO mutation surface:
// events append inside the transactions that create them and never change.
public static class AuditEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAuditEvents)
            .WithName("ListAuditEvents")
            .WithSummary("List audit events newest first (optional action/record type/entity/date filters, paged).");

        return group;
    }

    private static async Task<IResult> ListAuditEvents(
        IInsightsModule events,
        TenantContext tenant,
        CancellationToken ct,
        string? action = null,
        string? entityType = null,
        Guid? entityId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int? limit = null,
        int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var list = await events.ListAuditEventsAsync(action, entityType,
            entityId, from, to, take, skip, ct);
        return Results.Ok(list);
    }
}
