namespace Cluckwork.Api.Endpoints.Inventory;

using Cluckwork.Application.Features.Inventory;
using Cluckwork.Application.Features.Inventory.CreateInventoryItem;
using Cluckwork.Application.Features.Inventory.RecordAdjustment;
using Cluckwork.Application.Features.Inventory.RecordFeedUsage;
using Cluckwork.Application.Features.Inventory.RecordPurchase;
using Cluckwork.Application.Features.Inventory.UpdateInventoryItem;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

public static class InventoryEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    public static RouteGroupBuilder MapInventoryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/items", ListItems)
            .WithName("ListInventoryItems")
            .WithSummary("List inventory items with stock on hand. Active only by default; includeInactive=true adds deactivated items.");

        group.MapGet("/items/{id:guid}", GetItem)
            .WithName("GetInventoryItem")
            .WithSummary("Get a single inventory item (active or not).");

        group.MapPost("/items", CreateItem)
            .WithName("CreateInventoryItem")
            .WithSummary("Create an inventory item (name unique per farm, case-insensitive; category immutable).");

        group.MapPut("/items/{id:guid}", UpdateItem)
            .WithName("UpdateInventoryItem")
            .WithSummary("Rename an item or change its unit/default cost. Unit locks once stock has been received.");

        group.MapPost("/items/{id:guid}/deactivate", (Guid id, SetInventoryItemActiveHandler h, TenantContext t, CancellationToken ct) => SetActive(id, false, h, t, ct))
            .WithName("DeactivateInventoryItem")
            .WithSummary("Deactivate an item: it leaves pickers; lots, stock, and history are unaffected.");

        group.MapPost("/items/{id:guid}/activate", (Guid id, SetInventoryItemActiveHandler h, TenantContext t, CancellationToken ct) => SetActive(id, true, h, t, ct))
            .WithName("ActivateInventoryItem")
            .WithSummary("Reactivate a previously deactivated item.");

        group.MapPost("/items/{id:guid}/purchases", RecordPurchase)
            .WithName("RecordInventoryPurchase")
            .WithSummary("Receive stock: creates an inventory lot and its Purchase ledger row.");

        group.MapGet("/items/{id:guid}/lots", ListLots)
            .WithName("ListInventoryLots")
            .WithSummary("List an item's lots, newest received first.");

        group.MapGet("/items/{id:guid}/movements", ListMovements)
            .WithName("ListInventoryMovements")
            .WithSummary("Movement ledger for an item, newest first (paged).");

        group.MapPost("/items/{id:guid}/usage", RecordFeedUsage)
            .WithName("RecordFeedUsage")
            .WithSummary("Record feed consumed by a flock: drains lots FIFO, appends Usage ledger rows, estimates cost from lot costs.");

        group.MapPost("/items/{id:guid}/adjustments", RecordAdjustment)
            .WithName("RecordInventoryAdjustment")
            .WithSummary("Correct a lot's stock (signed Adjustment) or write it off (Discard) via a compensating ledger row; reason required.");

        group.MapGet("/usage", ListFeedUsage)
            .WithName("ListFeedUsage")
            .WithSummary("List feed usage records, newest first (optional flock/date filters, paged).");

        return group;
    }

    private static async Task<IResult> ListItems(
        IInventoryItemRepository items, IInventoryLotRepository lots,
        TenantContext tenant, CancellationToken ct,
        bool includeInactive = false)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var list = await items.ListAsync(includeInactive, ct);
        var stock = await lots.StockByItemAsync(ct);
        return Results.Ok(list.Select(i => ToResponse(i, stock.GetValueOrDefault(i.Id))));
    }

    private static async Task<IResult> GetItem(
        Guid id, IInventoryItemRepository items, IInventoryLotRepository lots,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var item = await items.GetByIdAsync(id, ct);
        if (item is null) return Results.NotFound();
        var stock = await lots.StockByItemAsync(ct);
        return Results.Ok(ToResponse(item, stock.GetValueOrDefault(item.Id)));
    }

    private static async Task<IResult> CreateItem(
        CreateInventoryItemRequest request,
        CreateInventoryItemHandler handler,
        IValidator<CreateInventoryItemCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateInventoryItemCommand(
            request.Name, request.Category, request.Unit, request.DefaultUnitCostMinorUnits);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/inventory/items/{result.Value}", new { Id = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> UpdateItem(
        Guid id,
        UpdateInventoryItemRequest request,
        UpdateInventoryItemHandler handler,
        IValidator<UpdateInventoryItemCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateInventoryItemCommand(
            id, request.Name, request.Unit, request.DefaultUnitCostMinorUnits);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static async Task<IResult> SetActive(
        Guid id, bool active, SetInventoryItemActiveHandler handler, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var result = await handler.HandleAsync(id, active, ct);
        return result.IsSuccess ? Results.NoContent() : MapFailure(result.Error);
    }

    private static async Task<IResult> RecordPurchase(
        Guid id,
        RecordPurchaseRequest request,
        RecordPurchaseHandler handler,
        IValidator<RecordPurchaseCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new RecordPurchaseCommand(
            id, request.ReceivedDate, request.Quantity, request.UnitCostMinorUnits,
            request.LotNumber, request.ExpiryDate, request.Note);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/inventory/items/{id}/lots", new { LotId = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> ListLots(
        Guid id, IInventoryLotRepository lots, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var list = await lots.ListByItemAsync(id, ct);
        return Results.Ok(list.Select(l => new InventoryLotResponse(
            l.Id, l.InventoryItemId, l.ReceivedDate, l.LotNumber, l.ExpiryDate,
            l.QuantityReceived, l.QuantityAvailable,
            l.UnitCost.MinorUnits, l.UnitCost.CurrencyCode, l.UnitCost.CurrencyMinorUnit)));
    }

    private static async Task<IResult> ListMovements(
        Guid id, IInventoryMovementRepository movements, TenantContext tenant, CancellationToken ct,
        int? limit = null, int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);
        var list = await movements.ListByItemAsync(id, take, skip, ct);
        return Results.Ok(list.Select(m => new InventoryMovementResponse(
            m.Id, m.InventoryItemId, m.InventoryLotId, m.Date, m.Type.ToString(),
            m.QuantityDelta, m.Unit, m.FlockId, m.Note)));
    }

    private static async Task<IResult> RecordFeedUsage(
        Guid id,
        RecordFeedUsageRequest request,
        RecordFeedUsageHandler handler,
        IValidator<RecordFeedUsageCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new RecordFeedUsageCommand(
            request.FlockId, id, request.Date, request.Quantity, request.Note);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess ? Results.Ok(result.Value) : MapFailure(result.Error);
    }

    private static async Task<IResult> RecordAdjustment(
        Guid id,
        RecordAdjustmentRequest request,
        RecordAdjustmentHandler handler,
        IValidator<RecordAdjustmentCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new RecordAdjustmentCommand(
            id, request.InventoryLotId, request.Date, request.Type,
            request.QuantityDelta, request.Reason);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        return result.IsSuccess
            ? Results.Created($"/api/v1/inventory/items/{id}/movements", new { MovementId = result.Value })
            : MapFailure(result.Error);
    }

    private static async Task<IResult> ListFeedUsage(
        IFeedUsageRepository usages, TenantContext tenant, CancellationToken ct,
        Guid? flockId = null, DateOnly? from = null, DateOnly? to = null,
        int? limit = null, int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);
        var list = await usages.ListAsync(flockId, from, to, take, skip, ct);
        return Results.Ok(list.Select(u => new FeedUsageResponse(
            u.Id, u.FlockId, u.InventoryItemId, u.Date, u.Quantity, u.Unit,
            u.EstimatedCost.MinorUnits, u.EstimatedCost.CurrencyCode, u.EstimatedCost.CurrencyMinorUnit,
            u.Note)));
    }

    private static IResult MapFailure(Cluckwork.Domain.Common.Error error)
    {
        if (error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        return error.Code is "InventoryItem.DuplicateName" or "InventoryItem.NotActive"
                or "InventoryItem.AlreadyActive" or "InventoryItem.UnitLocked"
            ? Results.Problem(error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code)
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
    }

    private static InventoryItemResponse ToResponse(InventoryItem i, decimal onHand) => new(
        i.Id, i.FarmId, i.Name, i.Category.ToString(), i.Unit,
        i.DefaultUnitCost?.MinorUnits, i.DefaultUnitCost?.CurrencyCode, i.DefaultUnitCost?.CurrencyMinorUnit,
        onHand, i.Active);
}

public sealed record InventoryItemResponse(
    Guid Id, Guid FarmId, string Name, string Category, string Unit,
    long? DefaultCostMinorUnits, string? DefaultCostCurrencyCode, int? DefaultCostCurrencyMinorUnit,
    decimal QuantityOnHand, bool Active);

public sealed record InventoryLotResponse(
    Guid Id, Guid InventoryItemId, DateOnly ReceivedDate, string? LotNumber, DateOnly? ExpiryDate,
    decimal QuantityReceived, decimal QuantityAvailable,
    long UnitCostMinorUnits, string UnitCostCurrencyCode, int UnitCostCurrencyMinorUnit);

public sealed record InventoryMovementResponse(
    Guid Id, Guid InventoryItemId, Guid? InventoryLotId, DateOnly Date, string Type,
    decimal QuantityDelta, string Unit, Guid? FlockId, string? Note);

public sealed record CreateInventoryItemRequest(
    string Name, string Category, string Unit, long? DefaultUnitCostMinorUnits);

public sealed record UpdateInventoryItemRequest(
    string Name, string Unit, long? DefaultUnitCostMinorUnits);

public sealed record RecordPurchaseRequest(
    DateOnly ReceivedDate, decimal Quantity, long? UnitCostMinorUnits,
    string? LotNumber, DateOnly? ExpiryDate, string? Note);

public sealed record RecordFeedUsageRequest(
    Guid FlockId, DateOnly Date, decimal Quantity, string? Note);

public sealed record RecordAdjustmentRequest(
    Guid InventoryLotId, DateOnly Date, string Type, decimal QuantityDelta, string Reason);

public sealed record FeedUsageResponse(
    Guid Id, Guid FlockId, Guid InventoryItemId, DateOnly Date, decimal Quantity, string Unit,
    long EstimatedCostMinorUnits, string CurrencyCode, int CurrencyMinorUnit, string? Note);
