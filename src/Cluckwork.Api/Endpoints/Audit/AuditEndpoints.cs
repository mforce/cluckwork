namespace Cluckwork.Api.Endpoints.Audit;

using Cluckwork.Application.Features.Audit;
using Cluckwork.Domain.Auditing;
using Cluckwork.Infrastructure.Persistence;

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
            .WithSummary("List audit events newest first (optional action/entity/date filters, paged).");

        return group;
    }

    private static async Task<IResult> ListAuditEvents(
        IAuditEventRepository events,
        TenantContext tenant,
        CancellationToken ct,
        string? action = null,
        Guid? entityId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int? limit = null,
        int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var list = await events.ListAsync(action, entityId, from, to, take, skip, ct);
        return Results.Ok(list.Select(ToResponse));
    }

    private static AuditEventResponse ToResponse(AuditEvent e) =>
        new(e.Id, e.OccurredAtUtc, e.ActorEmail, e.Action, e.EntityType, e.EntityId,
            e.Reason, e.DetailsJson);
}

public sealed record AuditEventResponse(
    Guid Id, DateTimeOffset OccurredAtUtc, string ActorEmail,
    string Action, string EntityType, Guid EntityId,
    string? Reason, string? DetailsJson);
