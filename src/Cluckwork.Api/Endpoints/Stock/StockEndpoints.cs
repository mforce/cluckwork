namespace Cluckwork.Api.Endpoints.Stock;

using Cluckwork.Api.Validation;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.EggLots.RecordEggLotMovement;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class StockEndpoints
{
    public static RouteGroupBuilder MapStockEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetStock)
            .WithName("GetStock")
            .WithSummary("Current egg stock by grade. Available excludes withdrawal-restricted lots, which sum separately.");

        // #101 — the ledger behind the cached balances. Production data:
        // open to any signed-in user, like the stock summary.
        group.MapGet("/lots", ListLots)
            .WithName("ListEggLots")
            .WithSummary("Egg lots newest-production-first (optional gradeId filter, paged).");

        group.MapGet("/lots/{id:guid}/movements", ListLotMovements)
            .WithName("ListEggLotMovements")
            .WithSummary("A lot's movement ledger, newest first — every change to its available quantity.");

        // #406 — the group's only write. Corrections are gated like adjust and
        // void (#73): Owner + Manager, not the recording tiers.
        group.MapPost("/lots/{id:guid}/movements", RecordLotMovement)
            .WithName("RecordEggLotMovement")
            .WithSummary("Write off lost stock or apply a recount against one lot — production is never restated.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        return group;
    }

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static async Task<IResult> ListLots(
        IEggLotRepository eggLots, TenantContext tenant, CancellationToken ct,
        Guid? gradeId = null, int? limit = null, int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);
        var lots = await eggLots.ListAsync(gradeId, take, skip, ct);
        return Results.Ok(lots.Select(l => new EggLotResponse(
            l.Id, l.EggGradeId, l.ProductionDate, l.QuantityProduced,
            l.QuantityAvailable, l.RestrictedUntil, l.DailyEntryId)));
    }

    private static async Task<IResult> ListLotMovements(
        Guid id, IEggLotRepository eggLots,
        Cluckwork.Application.Features.Eggs.IEggInventoryMovementRepository movements,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        // Foreign lots read as null through the tenant filter — same 404 as
        // nonexistent, no existence oracle.
        if (await eggLots.GetByIdAsync(id, ct) is null) return Results.NotFound();
        var list = await movements.ListByLotAsync(id, ct);
        return Results.Ok(list.Select(m => new EggMovementResponse(
            m.Id, m.MovementType.ToString(), m.QuantityDelta,
            m.ReferenceType, m.ReferenceId, m.Reason, m.CreatedAtUtc)));
    }

    private static async Task<IResult> RecordLotMovement(
        Guid id, RecordEggLotMovementRequest request,
        RecordEggLotMovementHandler handler,
        IValidator<RecordEggLotMovementCommand> validator,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new RecordEggLotMovementCommand(
            id, request.MovementType, request.QuantityDelta, request.Reason);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid) return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        if (result.IsSuccess) return Results.Ok(new RecordEggLotMovementResponse(
            result.Value.MovementId, result.Value.EggLotId, result.Value.MovementType,
            result.Value.QuantityDelta, result.Value.Reason, result.Value.CreatedAtUtc,
            result.Value.QuantityAvailable, result.Value.Version));

        // Foreign lots read as null through the tenant filter — same 404 as
        // nonexistent, no existence oracle.
        if (result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();

        return Results.Problem(result.Error.Description,
            statusCode: StatusCodes.Status422UnprocessableEntity, title: result.Error.Code);
    }

    private static async Task<IResult> GetStock(
        IEggLotRepository eggLots, TenantContext tenant, IFarmClock farmClock, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        // #35: the restriction boundary is the FARM-local date, not UTC — the
        // same boundary the allocation path (ConfirmSale) now uses, so a lot
        // can never read available here and restricted there.
        var rows = await eggLots.GetStockByGradeAsync(await farmClock.TodayAsync(ct), ct);
        return Results.Ok(rows.Select(r => new StockResponse(
            r.EggGradeId, r.GradeName, r.Available, r.Restricted)));
    }
}

public sealed record StockResponse(Guid EggGradeId, string GradeName, int Available, int Restricted);

public sealed record EggLotResponse(
    Guid Id, Guid EggGradeId, DateOnly ProductionDate, int QuantityProduced,
    int QuantityAvailable, DateOnly? RestrictedUntil, Guid? DailyEntryId);

public sealed record EggMovementResponse(
    Guid Id, string MovementType, int QuantityDelta,
    string ReferenceType, Guid ReferenceId, string? Reason, DateTimeOffset CreatedAtUtc);

public sealed record RecordEggLotMovementRequest(
    string MovementType, int QuantityDelta, string Reason);

// The movement as written plus the lot's new balance, so the SPA shows the
// resulting stock without a refetch.
public sealed record RecordEggLotMovementResponse(
    Guid MovementId, Guid EggLotId, string MovementType, int QuantityDelta,
    string? Reason, DateTimeOffset CreatedAtUtc, int QuantityAvailable, int Version);
