namespace Cluckwork.Api.Endpoints.Sales;

using Cluckwork.Api.Validation;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Application.Features.Sales.AddOrderItem;
using Cluckwork.Application.Features.Sales.CancelSalesOrder;
using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Application.Features.Sales.CreateSalesOrder;
using Cluckwork.Application.Features.Sales.RemoveOrderItem;
using Cluckwork.Application.Features.Sales.UpdateOrderItem;
using Cluckwork.Application.Features.Sales.VoidSale;
using FluentValidation;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;

public static class SaleEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    public static RouteGroupBuilder MapSaleEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/", CreateSalesOrder)
            .WithName("CreateSalesOrder")
            .WithSummary("Create a draft sales order for a customer (currency snapshotted from the account).")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        group.MapPost("/{id:guid}/items", AddOrderItem)
            .WithName("AddOrderItem")
            .WithSummary("Add a graded line item to a draft order.")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        group.MapPut("/{id:guid}/items/{itemId:guid}", UpdateOrderItem)
            .WithName("UpdateOrderItem")
            .WithSummary("Edit a line's quantity/unit price on a draft order.")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        group.MapDelete("/{id:guid}/items/{itemId:guid}", RemoveOrderItem)
            .WithName("RemoveOrderItem")
            .WithSummary("Remove a line from a draft order.")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        group.MapPost("/{id:guid}/cancel", CancelSalesOrder)
            .WithName("CancelSalesOrder")
            .WithSummary("Cancel a draft order (preserved as Cancelled, not deleted).")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        group.MapPost("/{id:guid}/confirm", ConfirmSale)
            .WithName("ConfirmSale")
            .WithSummary("Confirm a sales order and allocate egg lots via FIFO (online-only).")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        // Voiding undoes a confirmed sale — admin-only (#73). The draft
        // lifecycle (create/edit/cancel/confirm) stays open: workers sell.
        group.MapPost("/{id:guid}/void", VoidSale)
            .WithName("VoidSale")
            .WithSummary("Void a confirmed order, returning allocated stock to its source egg lots.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        // Order book carries financials (totals + line pricing). Gate the reads
        // to the sell-flow tier — workers build orders, ReadOnly is fenced out —
        // matching the writes above and the money reads on /payments (#127).
        group.MapGet("/{id:guid}", GetSalesOrder)
            .WithName("GetSalesOrder")
            .WithSummary("Get a sales order with its line items.")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        group.MapGet("/", ListSalesOrders)
            .WithName("ListSalesOrders")
            .WithSummary("List sales orders, newest first (optional status/customer filters, paged).")
            .RequireAuthorization(AuthPolicies.SalesFlow);

        return group;
    }

    private static async Task<IResult> CreateSalesOrder(
        CreateSalesOrderRequest request,
        CreateSalesOrderHandler handler,
        IValidator<CreateSalesOrderCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new CreateSalesOrderCommand(request.CustomerId, request.OrderDate);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        if (result.IsSuccess)
            return Results.Created($"/api/v1/sales/{result.Value}", new { Id = result.Value });
        return result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal)
            ? Results.NotFound()
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> AddOrderItem(
        Guid id,
        AddOrderItemRequest request,
        AddOrderItemHandler handler,
        IValidator<AddOrderItemCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new AddOrderItemCommand(
            id, request.ProductId, request.Quantity, request.Unit, request.UnitPriceMinorUnits);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        if (result.IsSuccess)
            return Results.Created($"/api/v1/sales/{id}", new { OrderId = id, ItemId = result.Value });
        if (result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        var status = result.Error.Code == "SalesOrder.NotDraft"
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status422UnprocessableEntity;
        return Results.Problem(result.Error.Description, statusCode: status, title: result.Error.Code);
    }

    private static IResult MapItemMutationFailure(Cluckwork.Domain.Common.Error error)
    {
        if (error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        return error.Code == "SalesOrder.NotDraft"
            ? Results.Problem(error.Description, statusCode: StatusCodes.Status409Conflict, title: error.Code)
            : Results.Problem(error.Description, statusCode: 422, title: error.Code);
    }

    private static async Task<IResult> UpdateOrderItem(
        Guid id,
        Guid itemId,
        UpdateOrderItemRequest request,
        UpdateOrderItemHandler handler,
        IValidator<UpdateOrderItemCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var command = new UpdateOrderItemCommand(id, itemId, request.Quantity, request.UnitPriceMinorUnits);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, ct);
        return result.IsSuccess ? Results.NoContent() : MapItemMutationFailure(result.Error);
    }

    private static async Task<IResult> RemoveOrderItem(
        Guid id,
        Guid itemId,
        RemoveOrderItemHandler handler,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var result = await handler.HandleAsync(id, itemId, ct);
        return result.IsSuccess ? Results.NoContent() : MapItemMutationFailure(result.Error);
    }

    private static async Task<IResult> CancelSalesOrder(
        Guid id,
        CancelSalesOrderHandler handler,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        var result = await handler.HandleAsync(id, ct);
        if (result.IsSuccess) return Results.NoContent();
        if (result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal))
            return Results.NotFound();
        return result.Error.Code == "SalesOrder.NotDraft"
            ? Results.Problem(result.Error.Description, statusCode: StatusCodes.Status409Conflict, title: result.Error.Code)
            : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
    }

    private static async Task<IResult> GetSalesOrder(
        Guid id, ISalesOrderRepository orders, TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var order = await orders.GetReadOnlyAsync(id, ct);
        return order is null ? Results.NotFound() : Results.Ok(ToResponse(order));
    }

    private static async Task<IResult> ListSalesOrders(
        ISalesOrderRepository orders, TenantContext tenant, CancellationToken ct,
        string? status = null, Guid? customerId = null,
        DateOnly? from = null, DateOnly? to = null,
        int? limit = null, int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();

        SalesOrderStatus? statusFilter = null;
        if (status is not null)
        {
            // IsDefined too: TryParse accepts any numeric ("999" parses fine).
            if (!Enum.TryParse<SalesOrderStatus>(status, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
                return ValidationResponse.Problem(new Dictionary<string, string[]>
                {
                    ["status"] = [$"Unknown status '{status}'."]
                });
            statusFilter = parsed;
        }

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);

        var list = await orders.ListAsync(statusFilter, customerId, from, to, take, skip, ct);
        return Results.Ok(list.Select(ToResponse));
    }

    private static SalesOrderResponse ToResponse(SalesOrder o) => new(
        o.Id, o.CustomerId, o.ReferenceNumber, o.OrderDate, o.Status.ToString(),
        o.TotalAmount.MinorUnits, o.TotalAmount.CurrencyCode, o.TotalAmount.CurrencyMinorUnit,
        o.VoidReason,
        o.Items.Select(i => new SalesOrderItemResponse(
            i.Id, i.ProductId, i.EggGradeId, i.Unit.ToString(), i.BaseUnitFactor,
            i.Quantity, i.QuantityBase,
            i.UnitPrice.MinorUnits, i.UnitPrice.CurrencyCode, i.UnitPrice.CurrencyMinorUnit)).ToList());

    private static async Task<IResult> VoidSale(
        Guid id,
        VoidSaleRequest request,
        VoidSaleHandler handler,
        IValidator<VoidSaleCommand> validator,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved)
            return Results.Unauthorized();

        var command = new VoidSaleCommand(id, request.Reason);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await handler.HandleAsync(command, tenant.AccountId, ct);
        if (result.IsSuccess)
            return Results.Ok(result.Value);

        // TenantMismatch → NotFound: don't reveal foreign-tenant existence.
        if (result.Error.Code.EndsWith(".NotFound", StringComparison.Ordinal)
            || result.Error.Code == "Tenant.Mismatch")
            return Results.NotFound();

        // Wrong lifecycle state is a genuine conflict; everything else
        // (missing allocation provenance, restore invariants) is a 422.
        var status = result.Error.Code is "SalesOrder.NotConfirmed" or "SalesOrder.AlreadyVoided"
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status422UnprocessableEntity;
        return Results.Problem(result.Error.Description, statusCode: status, title: result.Error.Code);
    }

    private static async Task<IResult> ConfirmSale(
        Guid id,
        ConfirmSaleHandler handler,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!tenant.IsResolved)
            return Results.Unauthorized();

        var result = await handler.HandleAsync(new ConfirmSaleCommand(id), tenant.AccountId, ct);

        // TenantMismatch is surfaced as NotFound to avoid revealing that the
        // resource exists but belongs to a different tenant.
        if (!result.IsSuccess)
        {
            if (result.Error.Code.EndsWith(".NotFound") || result.Error.Code == "Tenant.Mismatch")
                return Results.NotFound();

            // SalesOrder.NotDraft is a genuine state conflict (409); all other domain
            // errors (insufficient stock, withdrawal restriction, no items) are
            // business-rule violations that belong on 422 Unprocessable Entity.
            var status = result.Error.Code == "SalesOrder.NotDraft"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status422UnprocessableEntity;

            return Results.Problem(result.Error.Description, statusCode: status, title: result.Error.Code);
        }

        return Results.Ok(result.Value);
    }
}

// CurrencyMinorUnit included so clients render non-2-decimal currencies (JPY,
// KWD) correctly instead of assuming cents.
public sealed record SalesOrderResponse(
    Guid Id, Guid CustomerId, string ReferenceNumber, DateOnly OrderDate, string Status,
    long TotalMinorUnits, string CurrencyCode, int CurrencyMinorUnit,
    string? VoidReason,
    IReadOnlyList<SalesOrderItemResponse> Items);

public sealed record CreateSalesOrderRequest(Guid CustomerId, DateOnly OrderDate);

public sealed record VoidSaleRequest(string Reason);

public sealed record AddOrderItemRequest(
    Guid ProductId, int Quantity, string? Unit, long? UnitPriceMinorUnits);

public sealed record UpdateOrderItemRequest(int Quantity, long UnitPriceMinorUnits);

// Quantity is selling units; QuantityBase is individual eggs (Quantity ×
// BaseUnitFactor, snapshotted at line creation — spec §10.5/§9.7).
public sealed record SalesOrderItemResponse(
    Guid Id, Guid ProductId, Guid EggGradeId, string Unit, int BaseUnitFactor,
    int Quantity, int QuantityBase,
    long UnitPriceMinorUnits, string CurrencyCode, int CurrencyMinorUnit);
